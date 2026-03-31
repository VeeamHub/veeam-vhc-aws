"""Tests for WorkerHealthMonitor."""

import json
import os
from datetime import datetime, timedelta, timezone

from vhc_monitor.core.models import Severity, MonitorType
from vhc_monitor.core.patterns import ErrorPattern, PatternEngine
from vhc_monitor.monitors.worker_health import WorkerHealthMonitor

FIXTURES = os.path.join(os.path.dirname(__file__), "fixtures")


def _load_fixture(name: str):
    with open(os.path.join(FIXTURES, name)) as f:
        return json.load(f)


class MockVBAWSClient:
    def __init__(self, sessions=None):
        self._sessions = sessions or []

    def get_sessions(self, from_dt, to_dt):
        return self._sessions


def _default_config(**overrides):
    cfg = {
        "worker_health": {
            "lookback_hours": 24,
            "thresholds": {
                "session_failure_rate_warning": 0.1,
                "session_failure_rate_critical": 0.3,
                "retention_session_max_failures": 1,
                "zero_deleted_items_warning": True,
                "recurring_failure_threshold": 3,
            },
        }
    }
    cfg["worker_health"]["thresholds"].update(overrides)
    return cfg


def _subnet_engine():
    return PatternEngine([
        ErrorPattern(
            pattern=r"(?i)(?:cannot allocate|not enough free addresses|insufficient\s*free\s*addresses).*(?:subnet[- ]?(?P<subnet_id>subnet-[a-z0-9]+))",
            severity=Severity.CRITICAL,
            message="Subnet IP exhaustion",
            category="network",
            extract_fields=["subnet_id"],
            log_hint="/var/log/veeam/worker.log",
            remediation="Add a secondary subnet or increase CIDR range",
        ),
        ErrorPattern(
            pattern=r"(?i)access\s*key.*(?:invalid|expired|does not exist)",
            severity=Severity.CRITICAL,
            message="Credential failure",
            category="credential",
        ),
    ])


def test_healthy_sessions():
    sessions = [
        {"type": "BackupSession", "status": "Success", "result": {"message": ""}, "name": "b1"},
        {"type": "BackupSession", "status": "Success", "result": {"message": ""}, "name": "b2"},
        {"type": "RetentionSession", "status": "Success", "deletedItems": 3, "result": {"message": ""}, "name": "r1"},
    ]
    client = MockVBAWSClient(sessions=sessions)
    monitor = WorkerHealthMonitor(
        config=_default_config(),
        vbaws_client=client,
        pattern_engine=PatternEngine([]),
    )
    result = monitor.run()
    assert result.monitor == MonitorType.WORKER_HEALTH
    # No warning/critical findings for failure rate (metrics with OK severity are fine)
    rate_findings = [
        f for f in result.findings
        if "failure rate" in f.message.lower() and f.severity != Severity.OK
    ]
    assert len(rate_findings) == 0


def test_high_failure_rate():
    sessions = [
        {"type": "BackupSession", "status": "Failed", "result": {"message": "timeout"}, "name": "b1"},
        {"type": "BackupSession", "status": "Failed", "result": {"message": "timeout"}, "name": "b2"},
        {"type": "BackupSession", "status": "Success", "result": {"message": ""}, "name": "b3"},
    ]
    client = MockVBAWSClient(sessions=sessions)
    monitor = WorkerHealthMonitor(
        config=_default_config(),
        vbaws_client=client,
        pattern_engine=PatternEngine([]),
    )
    result = monitor.run()
    assert result.overall_severity in (Severity.WARNING, Severity.CRITICAL)
    assert any("failure rate" in f.message.lower() for f in result.findings)


def test_subnet_exhaustion_detection():
    sessions = _load_fixture("vbaws_sessions.json")
    # Patch fixture dates to be within lookback window
    recent = (datetime.now(timezone.utc) - timedelta(hours=1)).isoformat()
    for s in sessions:
        s["creationTime"] = recent
    client = MockVBAWSClient(sessions=sessions)
    engine = _subnet_engine()
    monitor = WorkerHealthMonitor(
        config=_default_config(),
        vbaws_client=client,
        pattern_engine=engine,
    )
    result = monitor.run()
    assert any(
        "subnet" in f.message.lower() and "exhaustion" in f.message.lower()
        for f in result.findings
    )
    subnet_metrics = [f for f in result.findings if f.metric_name == "veeam_vbaws_subnet_exhaustion"]
    assert len(subnet_metrics) == 1
    assert subnet_metrics[0].metric_value >= 1.0


def test_zero_deleted_items_warning():
    sessions = [
        {"type": "RetentionSession", "status": "Success", "deletedItems": 0, "result": {"message": ""}, "name": "r1"},
    ]
    client = MockVBAWSClient(sessions=sessions)
    monitor = WorkerHealthMonitor(
        config=_default_config(),
        vbaws_client=client,
        pattern_engine=PatternEngine([]),
    )
    result = monitor.run()
    assert any(
        "deleted nothing" in f.message.lower()
        for f in result.findings
    )


def test_no_retention_sessions():
    sessions = [
        {"type": "BackupSession", "status": "Success", "result": {"message": ""}, "name": "b1"},
    ]
    client = MockVBAWSClient(sessions=sessions)
    monitor = WorkerHealthMonitor(
        config=_default_config(),
        vbaws_client=client,
        pattern_engine=PatternEngine([]),
    )
    result = monitor.run()
    assert any(
        "no retention sessions" in f.message.lower()
        for f in result.findings
    )


def test_recurring_failure_pattern():
    """3 failures with same classified pattern should trigger recurring finding."""
    sessions = [
        {"type": "BackupSession", "status": "Failed",
         "result": {"message": "The AWS Access Key Id you provided does not exist"},
         "name": f"b{i}"}
        for i in range(4)
    ] + [
        {"type": "RetentionSession", "status": "Success", "deletedItems": 1,
         "result": {"message": ""}, "name": "r1"},
    ]
    engine = _subnet_engine()
    client = MockVBAWSClient(sessions=sessions)
    monitor = WorkerHealthMonitor(
        config=_default_config(),
        vbaws_client=client,
        pattern_engine=engine,
    )
    result = monitor.run()
    assert any(
        "recurring" in f.message.lower() and "credential" in f.message.lower()
        for f in result.findings
    )
