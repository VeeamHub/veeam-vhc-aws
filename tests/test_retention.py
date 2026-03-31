"""Tests for RetentionMonitor."""

import json
import os
from datetime import datetime, timedelta, timezone

from vhc_monitor.core.models import Severity, MonitorType
from vhc_monitor.core.patterns import PatternEngine
from vhc_monitor.monitors.retention import RetentionMonitor

FIXTURES = os.path.join(os.path.dirname(__file__), "fixtures")


def _load_fixture(name: str):
    with open(os.path.join(FIXTURES, name)) as f:
        return json.load(f)


class MockVBRClient:
    def __init__(self, jobs=None, backups=None, restore_points=None):
        self._jobs = jobs or []
        self._backups = backups or []
        self._restore_points = restore_points or []

    def get_jobs(self):
        return self._jobs

    def get_backups(self):
        return self._backups

    def get_restore_points(self, limit=500, offset=0):
        return self._restore_points[offset:offset + limit]


def _default_config(**overrides):
    cfg = {
        "retention": {
            "thresholds": {
                "overage_multiplier": 1.5,
                "max_age_multiplier": 1.5,
                "orphan_detection": True,
            },
        }
    }
    cfg["retention"]["thresholds"].update(overrides)
    return cfg


def test_normal_retention():
    """7 restore points for a job with retain_cycles=7 — should be OK."""
    now = datetime.now(timezone.utc)
    rps = [
        {"id": f"rp-{i}", "backupId": "backup-001", "name": "vm-web-01",
         "creationTime": (now - timedelta(days=i)).isoformat()}
        for i in range(7)
    ]
    client = MockVBRClient(
        jobs=[{"id": "job-001", "name": "Daily-Backup", "storage": {"retentionType": "cycles", "retainCycles": 7}}],
        backups=[{"id": "backup-001", "jobId": "job-001", "name": "Daily-Backup"}],
        restore_points=rps,
    )
    monitor = RetentionMonitor(
        config=_default_config(orphan_detection=False),
        vbr_client=client,
        pattern_engine=PatternEngine([]),
    )
    result = monitor.run()
    assert result.monitor == MonitorType.RETENTION
    # No violation findings
    violation_findings = [f for f in result.findings if f.severity in (Severity.WARNING, Severity.CRITICAL)]
    assert len(violation_findings) == 0


def test_cycles_overage():
    """11 restore points for a job with retain_cycles=7 — exceeds 7*1.5=10.5 threshold."""
    now = datetime.now(timezone.utc)
    rps = [
        {"id": f"rp-{i}", "backupId": "backup-001", "name": "vm-web-01",
         "creationTime": (now - timedelta(days=i)).isoformat()}
        for i in range(11)
    ]
    client = MockVBRClient(
        jobs=[{"id": "job-001", "name": "Daily-Backup", "storage": {"retentionType": "cycles", "retainCycles": 7}}],
        backups=[{"id": "backup-001", "jobId": "job-001", "name": "Daily-Backup"}],
        restore_points=rps,
    )
    monitor = RetentionMonitor(
        config=_default_config(orphan_detection=False),
        vbr_client=client,
        pattern_engine=PatternEngine([]),
    )
    result = monitor.run()
    assert result.overall_severity in (Severity.WARNING, Severity.CRITICAL)
    assert any(
        "overage" in f.message.lower() and f.severity in (Severity.WARNING, Severity.CRITICAL)
        for f in result.findings
    )


def test_days_overage():
    """Restore point older than retain_days * max_age_multiplier."""
    now = datetime.now(timezone.utc)
    very_old = now - timedelta(days=60)  # 60 days old, limit is 30 * 1.5 = 45 days
    rps = [
        {"id": "rp-001", "backupId": "backup-002", "name": "vm-db-01",
         "creationTime": very_old.isoformat()},
        {"id": "rp-002", "backupId": "backup-002", "name": "vm-db-01",
         "creationTime": (now - timedelta(days=7)).isoformat()},
    ]
    client = MockVBRClient(
        jobs=[{"id": "job-002", "name": "Weekly-Archive", "storage": {"retentionType": "days", "retainDays": 30}}],
        backups=[{"id": "backup-002", "jobId": "job-002", "name": "Weekly-Archive"}],
        restore_points=rps,
    )
    monitor = RetentionMonitor(
        config=_default_config(orphan_detection=False),
        vbr_client=client,
        pattern_engine=PatternEngine([]),
    )
    result = monitor.run()
    assert result.overall_severity == Severity.WARNING
    assert any(
        "age overage" in f.message.lower()
        for f in result.findings
    )


def test_orphan_detection():
    """Backup with a job_id that doesn't match any active job."""
    now = datetime.now(timezone.utc)
    rps = [
        {"id": "rp-001", "backupId": "backup-orphan", "name": "vm-legacy",
         "creationTime": (now - timedelta(days=1)).isoformat()},
    ]
    client = MockVBRClient(
        jobs=[{"id": "job-001", "name": "Active-Job", "storage": {"retentionType": "cycles", "retainCycles": 7}}],
        backups=[
            {"id": "backup-001", "jobId": "job-001", "name": "Active-Backup"},
            {"id": "backup-orphan", "jobId": "job-gone", "name": "Old-Orphan-Backup"},
        ],
        restore_points=rps,
    )
    monitor = RetentionMonitor(
        config=_default_config(),
        vbr_client=client,
        pattern_engine=PatternEngine([]),
    )
    result = monitor.run()
    assert any(
        "orphan" in f.message.lower() and f.severity == Severity.WARNING
        for f in result.findings
    )
    orphan_metric = [f for f in result.findings if f.metric_name == "veeam_retention_orphaned_backups"]
    assert len(orphan_metric) == 1
    assert orphan_metric[0].metric_value >= 1.0
