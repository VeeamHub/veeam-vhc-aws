"""Worker health monitor — checks VBAWS session health, retention failures, and patterns."""

import logging
import time
from collections import Counter, defaultdict
from datetime import datetime, timedelta, timezone

from vhc_monitor.core.models import Finding, MonitorResult, MonitorType, Severity
from vhc_monitor.monitors.base import BaseMonitor

logger = logging.getLogger("vhc_monitor.monitors.worker_health")


def _get_session_type(session: dict) -> str:
    """Get session type from VBAWS session, handling field name variations."""
    return str(session.get("sessionType", session.get("type", session.get("Type", "")))).lower()


def _get_session_state(session: dict) -> str:
    """Get session state from VBAWS session, handling field name variations."""
    return str(session.get("state", session.get("status", session.get("Status", "")))).lower()


def _get_session_name(session: dict) -> str:
    """Get session/policy name from VBAWS session."""
    return session.get("policyName", session.get("name", session.get("Name", "unknown")))


class WorkerHealthMonitor(BaseMonitor):
    """Monitor VBAWS worker health via session analysis."""

    @property
    def monitor_type(self) -> MonitorType:
        return MonitorType.WORKER_HEALTH

    @property
    def required_connections(self) -> list[str]:
        return ["vbaws"]

    def _get_config(self) -> dict:
        return self.config.get("worker_health", {})

    def _categorize_session(self, session: dict) -> str:
        """Categorize a session by its sessionType field."""
        session_type = _get_session_type(session)
        if "backup" in session_type or "policy" in session_type:
            return "backup"
        elif "restore" in session_type or "flr" in session_type:
            return "restore"
        elif "retention" in session_type:
            return "retention"
        return "other"

    def _get_error_text(self, session: dict) -> str:
        """Extract error text from a session, fetching logs if needed."""
        # First try inline fields
        result = session.get("result", session.get("Result", {}))
        if isinstance(result, dict):
            msg = result.get("message", result.get("Message", ""))
            if msg and msg.lower() not in ("failed", "warning", ""):
                return str(msg)
        reason = session.get("reason", "")
        if reason and reason != session.get("policyName", ""):
            return str(reason)

        # Inline fields are empty/generic — fetch session logs for actual error
        sid = session.get("id", "")
        if sid and self.vbaws_client:
            try:
                logs = self.vbaws_client.get_session_logs(sid)
                failed_logs = [
                    log.get("title", "") for log in logs
                    if log.get("status", "").lower() == "failed"
                    and log.get("title", "")
                    and "session finished" not in log.get("title", "").lower()
                ]
                if failed_logs:
                    return failed_logs[0]
            except Exception as e:
                logger.debug("Could not fetch logs for session %s: %s", sid[:8], e)

        return str(result) if result else ""

    def _filter_by_lookback(self, sessions: list[dict], from_dt: datetime) -> list[dict]:
        """Client-side date filter since VBAWS API may ignore date params."""
        filtered = []
        for s in sessions:
            ct = s.get("creationTime", s.get("CreationTime", ""))
            if not ct:
                filtered.append(s)
                continue
            try:
                session_time = datetime.fromisoformat(ct.replace("Z", "+00:00"))
                if session_time >= from_dt:
                    filtered.append(s)
            except (ValueError, TypeError):
                filtered.append(s)
        return filtered

    def run(self) -> MonitorResult:
        start = time.monotonic()
        findings: list[Finding] = []
        errors: list[str] = []
        cfg = self._get_config()
        thresholds = cfg.get("thresholds", {})
        lookback_hours = cfg.get("lookback_hours", 24)
        warning_rate = thresholds.get("session_failure_rate_warning", 0.1)
        critical_rate = thresholds.get("session_failure_rate_critical", 0.3)
        zero_deleted_warning = thresholds.get("zero_deleted_items_warning", True)
        recurring_threshold = thresholds.get("recurring_failure_threshold", 3)

        # --- 1. Session collection ---
        now = datetime.now(timezone.utc)
        from_dt = now - timedelta(hours=lookback_hours)

        try:
            sessions = self.vbaws_client.get_sessions(from_dt, now)
        except Exception as e:
            errors.append(f"Failed to fetch VBAWS sessions: {e}")
            sessions = []

        # Client-side date filter (VBAWS API may ignore from/to params)
        sessions = self._filter_by_lookback(sessions, from_dt)
        logger.info("VBAWS sessions in lookback window: %d (lookback=%dh)", len(sessions), lookback_hours)

        # Categorize sessions
        categorized: dict[str, list[dict]] = defaultdict(list)
        for session in sessions:
            cat = self._categorize_session(session)
            categorized[cat].append(session)

        logger.debug("Session categories: %s",
                     {k: len(v) for k, v in categorized.items()})

        # --- 2. Overall session health ---
        for session_type, type_sessions in categorized.items():
            total = len(type_sessions)
            failed_sessions = [
                s for s in type_sessions
                if _get_session_state(s) == "failed"
            ]
            failed = len(failed_sessions)
            failure_rate = failed / total if total > 0 else 0.0

            if failure_rate > critical_rate or failure_rate > warning_rate:
                severity = Severity.CRITICAL if failure_rate > critical_rate else Severity.WARNING
                label = "High" if failure_rate > critical_rate else "Elevated"

                findings.append(Finding(
                    severity=severity,
                    resource=f"session-type:{session_type}",
                    message=f"{label} {session_type} failure rate: {failure_rate:.0%} ({failed}/{total})",
                    details={"total": total, "failed": failed, "rate": failure_rate},
                ))

            # Per-policy latest-only: group sessions by policy,
            # only report if the MOST RECENT session for that policy is failed.
            # If a retry succeeded, the failure drops off automatically.
            by_policy: dict[str, list[dict]] = {}
            for s in type_sessions:
                policy = _get_session_name(s)
                by_policy.setdefault(policy, []).append(s)

            for policy, policy_sessions in by_policy.items():
                # Sort by creationTime DESC to get most recent first
                policy_sessions.sort(
                    key=lambda s: s.get("creationTime", s.get("CreationTime", "")),
                    reverse=True,
                )
                latest = policy_sessions[0]
                if _get_session_state(latest) != "failed":
                    continue

                error_text = self._get_error_text(latest)
                matched = None
                if self.pattern_engine and error_text:
                    matched = self.pattern_engine.classify(error_text)

                finding_sev = Severity.CRITICAL if matched else Severity.WARNING
                finding_msg = matched.message if matched else (error_text[:150] if error_text else "failed — check VBAWS console")

                findings.append(Finding(
                    severity=finding_sev,
                    resource=f"failed:{policy}",
                    message=finding_msg,
                    details={"error": error_text, "policy": policy,
                             "category": matched.category if matched else "unclassified",
                             "session_time": latest.get("creationTime", ""),
                             **({"remediation": matched.remediation} if matched and matched.remediation else {})},
                ))

            # Metrics
            findings.append(Finding(
                severity=Severity.OK,
                resource=f"session-type:{session_type}",
                message="session total metric",
                metric_name="veeam_vbaws_session_total",
                metric_value=float(total),
            ))
            findings.append(Finding(
                severity=Severity.OK,
                resource=f"session-type:{session_type}",
                message="session failed metric",
                metric_name="veeam_vbaws_session_failed",
                metric_value=float(failed),
            ))
            findings.append(Finding(
                severity=Severity.OK,
                resource=f"session-type:{session_type}",
                message="session failure rate metric",
                metric_name="veeam_vbaws_session_failure_rate",
                metric_value=failure_rate,
            ))

        # --- 3. Retention session deep inspection ---
        retention_sessions = categorized.get("retention", [])
        subnet_exhaustion_count = 0
        worker_health_inferred = 1.0
        retention_deleted_total = 0

        for session in retention_sessions:
            status = _get_session_state(session)

            if status == "failed":
                error_text = self._get_error_text(session)
                matched = None
                if self.pattern_engine:
                    matched = self.pattern_engine.classify(error_text)

                backup_name = _get_session_name(session)

                if matched and matched.category == "network":
                    subnet_exhaustion_count += 1
                    worker_health_inferred = 0.0
                    extracted = {}
                    if self.pattern_engine:
                        extracted = self.pattern_engine.extract(error_text, matched)
                    subnet_id = extracted.get("subnet_id", "unknown")
                    findings.append(Finding(
                        severity=Severity.CRITICAL,
                        resource=f"retention:{backup_name}",
                        message=f"Subnet IP exhaustion in {subnet_id}",
                        details={
                            "subnet_id": subnet_id,
                            "backup_name": backup_name,
                            "log_path": matched.log_hint,
                            "remediation": matched.remediation,
                            "error": error_text,
                        },
                    ))
                elif matched and matched.category == "credential":
                    worker_health_inferred = 0.0
                    findings.append(Finding(
                        severity=Severity.CRITICAL,
                        resource=f"retention:{backup_name}",
                        message="Expired AWS credentials",
                        details={"error": error_text},
                    ))
                else:
                    worker_health_inferred = 0.0
                    findings.append(Finding(
                        severity=Severity.CRITICAL,
                        resource=f"retention:{backup_name}",
                        message="Retention session failed — workers may not be deploying",
                        details={"error": error_text},
                    ))

            elif status == "success" or status == "completed":
                deleted_items = session.get("deletedItems", session.get("DeletedItems",
                               session.get("details", {}).get("deletedItems", None)))
                if deleted_items is not None:
                    retention_deleted_total += int(deleted_items)

                if zero_deleted_warning and deleted_items is not None and int(deleted_items) == 0:
                    backup_name = _get_session_name(session)
                    findings.append(Finding(
                        severity=Severity.WARNING,
                        resource=f"retention:{backup_name}",
                        message="Retention ran but deleted nothing — possible silent failure",
                        details={"backup_name": backup_name},
                    ))

        # Retention metrics
        findings.append(Finding(
            severity=Severity.OK,
            resource="retention-analysis",
            message="subnet exhaustion metric",
            metric_name="veeam_vbaws_subnet_exhaustion",
            metric_value=float(subnet_exhaustion_count),
        ))
        findings.append(Finding(
            severity=Severity.OK,
            resource="retention-analysis",
            message="worker health inferred metric",
            metric_name="veeam_vbaws_worker_health_inferred",
            metric_value=worker_health_inferred,
        ))
        findings.append(Finding(
            severity=Severity.OK,
            resource="retention-analysis",
            message="retention deleted items metric",
            metric_name="veeam_vbaws_retention_deleted_items_total",
            metric_value=float(retention_deleted_total),
        ))

        # --- 4. Session gap detection ---
        if not retention_sessions:
            findings.append(Finding(
                severity=Severity.WARNING,
                resource="retention-gap",
                message=f"No retention sessions in last {lookback_hours}h",
                details={"lookback_hours": lookback_hours},
            ))

        # --- 5. Failure pattern analysis ---
        all_failed = [
            s for s in sessions
            if _get_session_state(s) == "failed"
        ]

        category_counts: Counter = Counter()
        unclassified_errors: Counter = Counter()

        for session in all_failed:
            error_text = self._get_error_text(session)
            matched = None
            if self.pattern_engine:
                matched = self.pattern_engine.classify(error_text)

            if matched:
                category_counts[matched.category] += 1
            else:
                unclassified_errors[error_text] += 1

        for category, count in category_counts.items():
            if count >= recurring_threshold:
                findings.append(Finding(
                    severity=Severity.CRITICAL,
                    resource=f"pattern:{category}",
                    message=f"Recurring {category} failure: {count} occurrences",
                    details={"category": category, "count": count},
                ))

            # Metric for each category
            findings.append(Finding(
                severity=Severity.OK,
                resource=f"pattern:{category}",
                message="failure by category metric",
                metric_name="veeam_vbaws_failure_by_category",
                metric_value=float(count),
            ))

        for error_text, count in unclassified_errors.items():
            if count >= 3:
                findings.append(Finding(
                    severity=Severity.WARNING,
                    resource="pattern:unclassified",
                    message=f"Recurring unclassified failure: {count} occurrences",
                    details={"error": error_text[:200], "count": count},
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
