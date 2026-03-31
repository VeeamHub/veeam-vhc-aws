"""Finding state tracking for deduplication and resolved detection."""

import hashlib
import json
import logging
import os
from datetime import datetime, timezone
from typing import Optional

from vhc_monitor.core.models import Finding, MonitorResult, Severity

logger = logging.getLogger("vhc_monitor.state")


def _finding_key(finding: Finding, server: str, monitor: str) -> str:
    """Generate a stable key for a finding based on its identity (not severity/details)."""
    # Use resource + first 80 chars of message as the identity
    # This groups "the same problem" even if details change slightly
    identity = f"{server}|{monitor}|{finding.resource}|{finding.message[:80]}"
    return hashlib.md5(identity.encode(), usedforsecurity=False).hexdigest()


class FindingState:
    """Tracks which findings have been reported to avoid duplicate notifications."""

    def __init__(self, state_file: str = "./vhc-monitor-state.json") -> None:
        self._path = state_file
        self._state: dict = self._load()

    def _load(self) -> dict:
        if os.path.exists(self._path):
            try:
                with open(self._path, "r") as f:
                    return json.load(f)
            except (json.JSONDecodeError, OSError) as e:
                logger.warning("Could not load state file %s: %s", self._path, e)
        return {"findings": {}, "last_run": None}

    def _save(self) -> None:
        try:
            parent = os.path.dirname(os.path.abspath(self._path))
            os.makedirs(parent, exist_ok=True)
            with open(self._path, "w") as f:
                json.dump(self._state, f, indent=2, default=str)
        except OSError as e:
            logger.error("Could not save state file %s: %s", self._path, e)

    def process_results(self, results: list[MonitorResult]) -> tuple[
        list[MonitorResult], list[Finding]
    ]:
        """Compare current findings against previous state.

        Returns:
            - results with only NEW or still-active non-OK findings
            - list of RESOLVED findings (were reported before, now gone)
        """
        now = datetime.now(timezone.utc).isoformat()
        current_keys: set[str] = set()
        new_findings_by_result: dict[int, list[Finding]] = {}
        resolved: list[Finding] = []

        # Build set of current non-OK, non-metric finding keys
        for i, result in enumerate(results):
            new_findings_by_result[i] = []
            for finding in result.findings:
                if finding.metric_name or finding.severity == Severity.OK:
                    new_findings_by_result[i].append(finding)
                    continue

                key = _finding_key(finding, result.server, result.monitor.value)
                current_keys.add(key)

                prev = self._state["findings"].get(key)
                if prev is None:
                    # NEW finding — never seen before
                    new_findings_by_result[i].append(finding)
                    self._state["findings"][key] = {
                        "first_seen": now,
                        "last_seen": now,
                        "resource": finding.resource,
                        "message": finding.message[:100],
                        "severity": finding.severity.value,
                        "server": result.server,
                        "monitor": result.monitor.value,
                    }
                    logger.debug("New finding: %s", finding.resource)
                else:
                    # EXISTING finding — update last_seen but don't re-alert
                    prev["last_seen"] = now
                    prev["severity"] = finding.severity.value
                    # Still include in results for JSON/Prometheus output,
                    # but mark it so webhook can filter
                    finding.details = {**finding.details, "_seen_before": True}
                    new_findings_by_result[i].append(finding)

        # Find RESOLVED findings — were in state but not in current results
        stale_keys = []
        for key, info in self._state["findings"].items():
            if key not in current_keys:
                resolved.append(Finding(
                    severity=Severity.OK,
                    resource=info.get("resource", "unknown"),
                    message=f"RESOLVED: {info.get('message', 'unknown')}",
                    details={"resolved_at": now, "first_seen": info.get("first_seen"),
                             "was_severity": info.get("severity")},
                ))
                stale_keys.append(key)
                logger.info("Finding resolved: %s", info.get("resource", "unknown"))

        for key in stale_keys:
            del self._state["findings"][key]

        # Rebuild results with filtered findings
        filtered_results = []
        for i, result in enumerate(results):
            filtered = MonitorResult(
                monitor=result.monitor,
                timestamp=result.timestamp,
                duration_ms=result.duration_ms,
                overall_severity=result.overall_severity,
                findings=new_findings_by_result[i],
                server=result.server,
                errors=result.errors,
                metadata=result.metadata,
            )
            filtered_results.append(filtered)

        self._state["last_run"] = now
        self._save()

        return filtered_results, resolved
