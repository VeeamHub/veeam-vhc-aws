"""Tests for RepoHealthMonitor."""

import json
import os

from vhc_monitor.core.models import Severity, MonitorType
from vhc_monitor.core.patterns import ErrorPattern, PatternEngine
from vhc_monitor.monitors.repo_health import RepoHealthMonitor

FIXTURES = os.path.join(os.path.dirname(__file__), "fixtures")


def _load_fixture(name: str):
    with open(os.path.join(FIXTURES, name)) as f:
        return json.load(f)


class MockVBRClient:
    def __init__(self, repo_states=None, sessions=None):
        self._repo_states = repo_states or []
        self._sessions = sessions or []

    def get_repository_states(self):
        return self._repo_states

    def get_sessions(self, lookback_hours=48):
        return self._sessions


def _default_config(**overrides):
    cfg = {
        "repo_health": {
            "thresholds": {
                "free_space_warning_pct": 15,
                "free_space_critical_pct": 5,
            },
            "check_external_maintenance": True,
            "external_maintenance_lookback_hours": 48,
        }
    }
    cfg["repo_health"].update(overrides)
    return cfg


def _credential_engine():
    return PatternEngine([
        ErrorPattern(
            pattern=r"(?i)access\s*key.*(?:invalid|expired|does not exist)",
            severity=Severity.CRITICAL,
            message="AWS credential failure",
            category="credential",
        ),
        ErrorPattern(
            pattern=r"(?i)s3.*(?:timeout|connection refused|503)",
            severity=Severity.CRITICAL,
            message="S3 connectivity failure",
            category="s3",
        ),
        ErrorPattern(
            pattern=r"(?i)authentication failed|unauthorized|401",
            severity=Severity.CRITICAL,
            message="Auth failure",
            category="auth",
        ),
    ])


def test_healthy_repos():
    client = MockVBRClient(repo_states=[
        {"name": "Healthy-Repo", "type": "WinLocal", "capacityGB": 1000, "freeGB": 500},
    ])
    monitor = RepoHealthMonitor(
        config=_default_config(check_external_maintenance=False),
        vbr_client=client,
        pattern_engine=PatternEngine([]),
    )
    result = monitor.run()
    assert result.monitor == MonitorType.REPO_HEALTH
    assert result.overall_severity == Severity.OK
    status_findings = [f for f in result.findings if f.metric_name is None]
    assert all(f.severity == Severity.OK for f in status_findings)


def test_low_space_warning():
    client = MockVBRClient(repo_states=[
        {"name": "Low-Repo", "type": "WinLocal", "capacityGB": 500, "freeGB": 50},
    ])
    monitor = RepoHealthMonitor(
        config=_default_config(check_external_maintenance=False),
        vbr_client=client,
        pattern_engine=PatternEngine([]),
    )
    result = monitor.run()
    assert result.overall_severity == Severity.WARNING
    assert any(
        f.severity == Severity.WARNING and "low on space" in f.message.lower()
        for f in result.findings
    )


def test_low_space_critical():
    client = MockVBRClient(repo_states=[
        {"name": "Critical-Repo", "type": "WinLocal", "capacityGB": 200, "freeGB": 5},
    ])
    monitor = RepoHealthMonitor(
        config=_default_config(check_external_maintenance=False),
        vbr_client=client,
        pattern_engine=PatternEngine([]),
    )
    result = monitor.run()
    assert result.overall_severity == Severity.CRITICAL
    assert any(
        f.severity == Severity.CRITICAL and "critically low" in f.message.lower()
        for f in result.findings
    )


def test_unreachable_repo():
    client = MockVBRClient(repo_states=[
        {"name": "Dead-Repo", "type": "WinLocal", "capacityGB": 0, "freeGB": 0},
    ])
    monitor = RepoHealthMonitor(
        config=_default_config(check_external_maintenance=False),
        vbr_client=client,
        pattern_engine=PatternEngine([]),
    )
    result = monitor.run()
    assert result.overall_severity == Severity.ERROR
    assert any(
        f.severity == Severity.ERROR and "unreachable" in f.message.lower()
        for f in result.findings
    )


def test_expired_credential_detection():
    sessions = _load_fixture("sessions_external_maintenance.json")
    client = MockVBRClient(
        repo_states=[
            {"name": "S3-Repo", "type": "ExternalS3", "capacityGB": 5000, "freeGB": 4000},
        ],
        sessions=sessions,
    )
    engine = _credential_engine()
    monitor = RepoHealthMonitor(
        config=_default_config(),
        vbr_client=client,
        pattern_engine=engine,
    )
    result = monitor.run()
    assert result.overall_severity == Severity.CRITICAL
    assert any(
        f.severity == Severity.CRITICAL and "credential" in f.message.lower()
        for f in result.findings
    )
    # Check metric
    cred_metrics = [f for f in result.findings if f.metric_name == "veeam_repo_credential_expired"]
    assert len(cred_metrics) == 1
    assert cred_metrics[0].metric_value >= 1.0


def test_no_external_maintenance_sessions():
    client = MockVBRClient(
        repo_states=[
            {"name": "S3-Repo", "type": "ExternalS3", "capacityGB": 5000, "freeGB": 4000},
        ],
        sessions=[],  # No sessions at all
    )
    monitor = RepoHealthMonitor(
        config=_default_config(),
        vbr_client=client,
        pattern_engine=PatternEngine([]),
    )
    result = monitor.run()
    assert any(
        "no external maintenance sessions" in f.message.lower()
        for f in result.findings
    )
