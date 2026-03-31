"""Retention monitor — checks restore point retention compliance and orphaned backups."""

import time
from collections import defaultdict
from datetime import datetime, timedelta, timezone

from vhc_monitor.core.models import Finding, MonitorResult, MonitorType, Severity
from vhc_monitor.monitors.base import BaseMonitor


class RetentionMonitor(BaseMonitor):
    """Monitor restore point retention compliance."""

    @property
    def monitor_type(self) -> MonitorType:
        return MonitorType.RETENTION

    @property
    def required_connections(self) -> list[str]:
        return ["vbr"]

    def _get_config(self) -> dict:
        return self.config.get("retention", {})

    def run(self) -> MonitorResult:
        start = time.monotonic()
        findings: list[Finding] = []
        errors: list[str] = []
        cfg = self._get_config()
        thresholds = cfg.get("thresholds", {})
        overage_multiplier = thresholds.get("overage_multiplier", 1.5)
        max_age_multiplier = thresholds.get("max_age_multiplier", 1.5)
        orphan_detection = thresholds.get("orphan_detection", True)
        exclude_backups = set(cfg.get("exclude_backups", []))

        # --- 1. Get jobs ---
        try:
            jobs = self.vbr_client.get_jobs()
        except Exception as e:
            errors.append(f"Failed to fetch jobs: {e}")
            jobs = []

        job_map: dict[str, dict] = {}
        for job in jobs:
            job_id = job.get("id", job.get("Id", ""))
            job_name = job.get("name", job.get("Name", "unknown"))
            storage = job.get("storage", job.get("Storage", {}))
            schedule = job.get("schedule", job.get("Schedule", {}))

            # Determine retention type and value
            retention_type = storage.get("retentionType", storage.get("RetentionType", "cycles"))
            retain_count = storage.get("retainCycles", storage.get("RetainCycles",
                           storage.get("retainCount", storage.get("RetainCount", 14))))
            retain_days = storage.get("retainDays", storage.get("RetainDays", 14))

            job_map[job_id] = {
                "name": job_name,
                "retention_type": str(retention_type).lower(),
                "retain_cycles": int(retain_count) if retain_count else 14,
                "retain_days": int(retain_days) if retain_days else 14,
            }

        # --- 2. Get backups ---
        try:
            backups = self.vbr_client.get_backups()
        except Exception as e:
            errors.append(f"Failed to fetch backups: {e}")
            backups = []

        backup_map: dict[str, dict] = {}
        for backup in backups:
            backup_id = backup.get("id", backup.get("Id", ""))
            backup_job_id = backup.get("jobId", backup.get("JobId", ""))
            backup_name = backup.get("name", backup.get("Name", "unknown"))
            policy_id = backup.get("policyUniqueId", backup.get("PolicyUniqueId", ""))
            platform = backup.get("platformName", backup.get("PlatformName", ""))
            backup_map[backup_id] = {
                "job_id": backup_job_id,
                "name": backup_name,
                "policy_id": policy_id,
                "platform": platform,
            }

        # --- 3. Get restore points (paginated) ---
        all_restore_points: list[dict] = []
        offset = 0
        limit = 500
        while True:
            try:
                batch = self.vbr_client.get_restore_points(limit=limit, offset=offset)
            except Exception as e:
                errors.append(f"Failed to fetch restore points at offset {offset}: {e}")
                break
            if not batch:
                break
            all_restore_points.extend(batch)
            if len(batch) < limit:
                break
            offset += limit

        # Group restore points by VM/backup object
        vm_points: dict[str, list[dict]] = defaultdict(list)
        for rp in all_restore_points:
            vm_name = rp.get("name", rp.get("Name",
                     rp.get("vmName", rp.get("VmName", "unknown"))))
            vm_points[vm_name].append(rp)

        # --- 4. Check retention compliance ---
        now = datetime.now(timezone.utc)
        active_job_ids = set(job_map.keys())

        for vm_name, points in vm_points.items():
            # Sort by creation time
            for p in points:
                ct = p.get("creationTime", p.get("CreationTime", ""))
                if isinstance(ct, str) and ct:
                    try:
                        p["_parsed_time"] = datetime.fromisoformat(ct.replace("Z", "+00:00"))
                    except (ValueError, TypeError):
                        p["_parsed_time"] = now
                else:
                    p["_parsed_time"] = now

            points.sort(key=lambda p: p["_parsed_time"])

            # Find parent job via backup
            backup_id = points[0].get("backupId", points[0].get("BackupId", ""))
            parent_backup = backup_map.get(backup_id, {})
            parent_job_id = parent_backup.get("job_id", "")
            job_info = job_map.get(parent_job_id, {})

            if not job_info:
                # Try matching by any other means — skip if no job found
                continue

            retention_type = job_info.get("retention_type", "cycles")
            actual_count = len(points)

            if retention_type == "cycles":
                expected_max = job_info.get("retain_cycles", 14)
                threshold = int(expected_max * overage_multiplier)

                if actual_count > threshold:
                    severity = Severity.CRITICAL if actual_count > expected_max * 2 else Severity.WARNING
                    findings.append(Finding(
                        severity=severity,
                        resource=f"vm:{vm_name}",
                        message=f"Retention overage: {actual_count} restore points vs {expected_max} expected",
                        details={
                            "actual": actual_count,
                            "expected": expected_max,
                            "threshold": threshold,
                            "job": job_info["name"],
                        },
                    ))
                else:
                    findings.append(Finding(
                        severity=Severity.OK,
                        resource=f"vm:{vm_name}",
                        message=f"Retention OK: {actual_count}/{expected_max} points",
                    ))

                # Metrics
                findings.append(Finding(
                    severity=Severity.OK,
                    resource=f"vm:{vm_name}",
                    message="actual points metric",
                    metric_name="veeam_retention_actual_points",
                    metric_value=float(actual_count),
                ))
                findings.append(Finding(
                    severity=Severity.OK,
                    resource=f"vm:{vm_name}",
                    message="expected points metric",
                    metric_name="veeam_retention_expected_points",
                    metric_value=float(expected_max),
                ))
                findings.append(Finding(
                    severity=Severity.OK,
                    resource=f"vm:{vm_name}",
                    message="violation metric",
                    metric_name="veeam_retention_violation",
                    metric_value=1.0 if actual_count > threshold else 0.0,
                ))

            else:  # days-based
                retain_days = job_info.get("retain_days", 14)
                oldest_allowed = now - timedelta(days=int(retain_days * max_age_multiplier))
                oldest_actual = points[0]["_parsed_time"]
                age_hours = (now - oldest_actual).total_seconds() / 3600

                if oldest_actual < oldest_allowed:
                    findings.append(Finding(
                        severity=Severity.WARNING,
                        resource=f"vm:{vm_name}",
                        message=f"Retention age overage: oldest point is {age_hours:.0f}h old, limit is {retain_days * max_age_multiplier * 24:.0f}h",
                        details={
                            "oldest_point": oldest_actual.isoformat(),
                            "retain_days": retain_days,
                            "age_hours": age_hours,
                            "job": job_info["name"],
                        },
                    ))
                else:
                    findings.append(Finding(
                        severity=Severity.OK,
                        resource=f"vm:{vm_name}",
                        message=f"Retention age OK: oldest point {age_hours:.0f}h",
                    ))

                findings.append(Finding(
                    severity=Severity.OK,
                    resource=f"vm:{vm_name}",
                    message="oldest point age metric",
                    metric_name="veeam_retention_oldest_point_age_hours",
                    metric_value=age_hours,
                ))

        # --- 5. Orphan detection ---
        orphan_count = 0
        if orphan_detection:
            for backup_id, backup_info in backup_map.items():
                if not backup_info["job_id"]:
                    continue
                if backup_info["job_id"] in active_job_ids:
                    continue
                # Skip externally managed backups (Kasten, etc.)
                if backup_info.get("policy_id"):
                    continue
                # Skip explicitly excluded backups
                if backup_info["name"] in exclude_backups:
                    continue

                rp_count = sum(
                    1 for rp in all_restore_points
                    if rp.get("backupId", rp.get("BackupId", "")) == backup_id
                )
                orphan_count += 1
                findings.append(Finding(
                    severity=Severity.WARNING,
                    resource=f"backup:{backup_info['name']}",
                    message=f"Orphaned backup '{backup_info['name']}': {rp_count} restore points, no active job",
                    details={
                        "backup_id": backup_id,
                        "job_id": backup_info["job_id"],
                        "restore_point_count": rp_count,
                    },
                ))

            findings.append(Finding(
                severity=Severity.OK,
                resource="orphan-detection",
                message="orphaned backups metric",
                metric_name="veeam_retention_orphaned_backups",
                metric_value=float(orphan_count),
            ))

        # --- Overall severity ---
        severity_rank = {Severity.OK: 0, Severity.WARNING: 1, Severity.CRITICAL: 2, Severity.ERROR: 3}
        overall = Severity.OK
        for f in findings:
            if severity_rank.get(f.severity, 0) > severity_rank.get(overall, 0):
                overall = f.severity

        duration = int((time.monotonic() - start) * 1000)
        return MonitorResult(
            monitor=self.monitor_type,
            timestamp=datetime.now(timezone.utc),
            duration_ms=duration,
            overall_severity=overall,
            findings=findings,
            errors=errors,
        )
