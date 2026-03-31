"""Repository health monitor — checks capacity, external maintenance, and credential status."""

import logging
import time
from datetime import datetime, timezone

from vhc_monitor.core.models import Finding, MonitorResult, MonitorType, Severity
from vhc_monitor.monitors.base import BaseMonitor

logger = logging.getLogger("vhc_monitor.monitors.repo_health")

_SEVERITY_RANK = {Severity.OK: 0, Severity.WARNING: 1, Severity.CRITICAL: 2, Severity.ERROR: 3}


def _severity_rank(s: Severity) -> int:
    return _SEVERITY_RANK.get(s, 0)


class RepoHealthMonitor(BaseMonitor):
    """Monitor repository capacity and external maintenance sessions."""

    @property
    def monitor_type(self) -> MonitorType:
        return MonitorType.REPO_HEALTH

    @property
    def required_connections(self) -> list[str]:
        return ["vbr"]

    def _get_config(self) -> dict:
        return self.config.get("repo_health", {})

    def run(self) -> MonitorResult:
        start = time.monotonic()
        findings: list[Finding] = []
        errors: list[str] = []
        cfg = self._get_config()
        thresholds = cfg.get("thresholds", {})
        warning_pct = thresholds.get("free_space_warning_pct", 15)
        critical_pct = thresholds.get("free_space_critical_pct", 5)

        # --- 1. Repository capacity check ---
        try:
            repo_states = self.vbr_client.get_repository_states()
        except Exception as e:
            errors.append(f"Failed to fetch repository states: {e}")
            repo_states = []

        has_external_repos = False

        for repo in repo_states:
            repo_name = repo.get("name", repo.get("Name", "unknown"))
            capacity_gb = repo.get("capacityGB", repo.get("CapacityGB", 0))
            free_gb = repo.get("freeGB", repo.get("FreeGB", 0))
            repo_type = repo.get("type", repo.get("Type", ""))

            if repo_type and "external" in str(repo_type).lower():
                has_external_repos = True

            # Unreachable repo
            if not capacity_gb:
                findings.append(Finding(
                    severity=Severity.ERROR,
                    resource=f"repo:{repo_name}",
                    message="repo unreachable",
                    details={"capacityGB": capacity_gb, "freeGB": free_gb},
                ))
                findings.append(Finding(
                    severity=Severity.OK,
                    resource=f"repo:{repo_name}",
                    message="repo health metric",
                    metric_name="veeam_repo_healthy",
                    metric_value=0.0,
                ))
                continue

            free_pct = free_gb / capacity_gb * 100

            if free_pct < critical_pct:
                findings.append(Finding(
                    severity=Severity.CRITICAL,
                    resource=f"repo:{repo_name}",
                    message=f"Repository critically low on space: {free_pct:.1f}% free",
                    details={"capacityGB": capacity_gb, "freeGB": free_gb, "freePct": free_pct},
                ))
            elif free_pct < warning_pct:
                findings.append(Finding(
                    severity=Severity.WARNING,
                    resource=f"repo:{repo_name}",
                    message=f"Repository low on space: {free_pct:.1f}% free",
                    details={"capacityGB": capacity_gb, "freeGB": free_gb, "freePct": free_pct},
                ))
            else:
                findings.append(Finding(
                    severity=Severity.OK,
                    resource=f"repo:{repo_name}",
                    message=f"Repository healthy: {free_pct:.1f}% free",
                    details={"capacityGB": capacity_gb, "freeGB": free_gb, "freePct": free_pct},
                ))

            # Metric findings
            findings.append(Finding(
                severity=Severity.OK,
                resource=f"repo:{repo_name}",
                message="capacity metric",
                metric_name="veeam_repo_capacity_gb",
                metric_value=float(capacity_gb),
            ))
            findings.append(Finding(
                severity=Severity.OK,
                resource=f"repo:{repo_name}",
                message="free space metric",
                metric_name="veeam_repo_free_gb",
                metric_value=float(free_gb),
            ))
            findings.append(Finding(
                severity=Severity.OK,
                resource=f"repo:{repo_name}",
                message="free pct metric",
                metric_name="veeam_repo_free_pct",
                metric_value=free_pct,
            ))
            findings.append(Finding(
                severity=Severity.OK,
                resource=f"repo:{repo_name}",
                message="repo health metric",
                metric_name="veeam_repo_healthy",
                metric_value=1.0 if free_pct >= critical_pct else 0.0,
            ))

        # --- 2. Session-based repo issue detection ---
        # Check ALL session types that can indicate repository problems:
        # - ExternalMaintenance: S3/credential failures on object storage repos
        # - ConfigurationResynchronize: database resync failures (credential, connectivity)
        # - RepositoryRescan: explicit rescan failures
        lookback_hours = cfg.get("session_lookback_hours",
                                 cfg.get("external_maintenance_lookback_hours", 48))
        credential_expired_count = 0
        session_warning_count = 0

        # Session types that indicate repo-related issues
        _REPO_SESSION_TYPES = {
            "externalmaintenance", "externalmaintenancesession",
            "configurationresynchronize",
            "repositoryrescan", "repositorymaintenance",
        }

        try:
            sessions = self.vbr_client.get_sessions(lookback_hours=lookback_hours)
        except Exception as e:
            errors.append(f"Failed to fetch sessions: {e}")
            sessions = []

        # Group problem sessions by type+status to avoid duplicate findings
        # key: (session_type, result_status, pattern_category or "")
        # value: {"count": N, "worst_severity": Severity, "error_text": str, "session_name": str}
        session_issues: dict[tuple, dict] = {}

        for session in sessions:
            session_type = str(
                session.get("sessionType", session.get("type", session.get("Type", "")))
            ).lower()

            if session_type not in _REPO_SESSION_TYPES:
                continue

            result_obj = session.get("result", session.get("Result", {}))
            if isinstance(result_obj, dict):
                result_status = str(result_obj.get("result", "")).lower()
                error_text = result_obj.get("message", result_obj.get("Message", ""))
            else:
                result_status = str(result_obj).lower()
                error_text = str(result_obj)

            if result_status not in ("warning", "failed"):
                continue

            session_warning_count += 1
            session_name = session.get("name", session.get("Name", "unknown"))
            severity = Severity.CRITICAL if result_status == "failed" else Severity.WARNING

            matched = None
            pattern_cat = ""
            if self.pattern_engine and error_text:
                matched = self.pattern_engine.classify(error_text)
                if matched:
                    pattern_cat = matched.category
                    if pattern_cat == "credential":
                        credential_expired_count += 1
                        severity = Severity.CRITICAL
                    elif pattern_cat in ("auth", "s3"):
                        severity = Severity.CRITICAL

            key = (session_type, result_status, pattern_cat)
            if key not in session_issues:
                session_issues[key] = {
                    "count": 0, "severity": severity,
                    "error_text": error_text, "session_name": session_name,
                }
            session_issues[key]["count"] += 1
            # Keep worst severity
            if _severity_rank(severity) > _severity_rank(session_issues[key]["severity"]):
                session_issues[key]["severity"] = severity

        # Emit one finding per unique issue type
        for (session_type, result_status, pattern_cat), info in session_issues.items():
            count = info["count"]
            count_suffix = f" ({count}x in last {lookback_hours}h)" if count > 1 else ""

            if pattern_cat == "credential":
                msg = f"Credential failure in {session_type}{count_suffix}: {info['error_text']}"
            elif pattern_cat in ("auth", "s3"):
                msg = f"S3/auth failure in {session_type}{count_suffix}: {info['error_text']}"
            else:
                detail = info["error_text"] or "check VBR console for details"
                msg = f"{session_type} {result_status}{count_suffix}: {detail}"

            findings.append(Finding(
                severity=info["severity"],
                resource=f"session:{info['session_name']}",
                message=msg,
                details={"session_type": session_type, "count": count,
                          "error": info["error_text"]},
            ))

            logger.info("Repo session issue: %s %s (%dx): %s",
                        session_type, result_status, count,
                        info["error_text"] or "no detail")

        if not sessions and has_external_repos:
            findings.append(Finding(
                severity=Severity.WARNING,
                resource="sessions",
                message="No sessions found in lookback window",
                details={"lookback_hours": lookback_hours},
            ))

        # Metrics
        findings.append(Finding(
            severity=Severity.OK,
            resource="session-health",
            message="repo session warnings metric",
            metric_name="veeam_repo_session_warnings",
            metric_value=float(session_warning_count),
        ))
        findings.append(Finding(
            severity=Severity.OK,
            resource="session-health",
            message="credential expired metric",
            metric_name="veeam_repo_credential_expired",
            metric_value=float(credential_expired_count),
        ))

        # --- 3. Scale-out backup repository check ---
        try:
            sobrs = self.vbr_client.get_scaleout_repositories()
        except Exception as e:
            errors.append(f"Failed to fetch scale-out repositories: {e}")
            sobrs = []

        for sobr in sobrs:
            sobr_name = sobr.get("name", sobr.get("Name", "unknown"))
            perf_tier = sobr.get("performanceTier", {})
            cap_tier = sobr.get("capacityTier", {})
            archive_tier = sobr.get("archiveTier", {})

            # Check performance tier extents
            perf_extents = perf_tier.get("performanceExtents",
                           perf_tier.get("extents", []))
            for extent in perf_extents:
                extent_name = extent.get("name", extent.get("Name", "unknown"))
                extent_status = str(extent.get("status", extent.get("Status", ""))).lower()

                if extent_status in ("maintenance", "evacuate", "sealed"):
                    findings.append(Finding(
                        severity=Severity.WARNING,
                        resource=f"sobr:{sobr_name}/extent:{extent_name}",
                        message=f"SOBR extent in {extent_status} mode",
                        details={"sobr": sobr_name, "extent": extent_name, "status": extent_status},
                    ))
                elif extent_status in ("ressyncrequired",):
                    findings.append(Finding(
                        severity=Severity.CRITICAL,
                        resource=f"sobr:{sobr_name}/extent:{extent_name}",
                        message="SOBR extent requires resync",
                        details={"sobr": sobr_name, "extent": extent_name, "status": extent_status},
                    ))
                else:
                    findings.append(Finding(
                        severity=Severity.OK,
                        resource=f"sobr:{sobr_name}/extent:{extent_name}",
                        message=f"SOBR extent healthy ({extent_status})",
                        details={"sobr": sobr_name, "extent": extent_name, "status": extent_status},
                    ))

            # Check capacity tier enabled status
            cap_enabled = cap_tier.get("enabled", cap_tier.get("Enabled", False))
            if cap_enabled:
                findings.append(Finding(
                    severity=Severity.OK,
                    resource=f"sobr:{sobr_name}/capacity-tier",
                    message="Capacity tier enabled",
                ))
            else:
                findings.append(Finding(
                    severity=Severity.OK,
                    resource=f"sobr:{sobr_name}/capacity-tier",
                    message="Capacity tier not configured",
                ))

            # Check archive tier enabled status
            arch_enabled = archive_tier.get("enabled", archive_tier.get("Enabled", False))
            if arch_enabled:
                findings.append(Finding(
                    severity=Severity.OK,
                    resource=f"sobr:{sobr_name}/archive-tier",
                    message="Archive tier enabled",
                ))

            # Metrics
            findings.append(Finding(
                severity=Severity.OK,
                resource=f"sobr:{sobr_name}",
                message="SOBR extent count metric",
                metric_name="veeam_sobr_extent_count",
                metric_value=float(len(perf_extents)),
            ))

            logger.info("SOBR '%s': %d performance extents, capacity_tier=%s, archive_tier=%s",
                        sobr_name, len(perf_extents),
                        "enabled" if cap_enabled else "disabled",
                        "enabled" if arch_enabled else "disabled")

        if sobrs:
            logger.info("Checked %d scale-out backup repositories", len(sobrs))

        # --- Overall severity ---
        overall = Severity.OK
        for f in findings:
            if _severity_rank(f.severity) > _severity_rank(overall):
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
