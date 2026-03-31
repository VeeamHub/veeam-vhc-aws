"""Tests for PatternEngine."""

from vhc_monitor.core.models import Severity
from vhc_monitor.core.patterns import ErrorPattern, PatternEngine


def _build_engine() -> PatternEngine:
    """Build a PatternEngine with standard test patterns."""
    return PatternEngine([
        ErrorPattern(
            pattern=r"(?i)access\s*key.*(?:invalid|expired|does not exist)",
            severity=Severity.CRITICAL,
            message="AWS credential failure",
            category="credential",
            extract_fields=["key_id"],
        ),
        ErrorPattern(
            pattern=r"(?i)(?:cannot allocate|insufficient\s*free\s*addresses).*(?:subnet[- ]?(?P<subnet_id>subnet-[a-z0-9]+))",
            severity=Severity.CRITICAL,
            message="Subnet IP exhaustion",
            category="network",
            extract_fields=["subnet_id"],
            log_hint="/var/log/veeam/worker.log",
            remediation="Add a secondary subnet or increase CIDR range",
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
            message="Authentication failure",
            category="auth",
        ),
    ])


def test_classify_credential_pattern():
    engine = _build_engine()
    result = engine.classify("The AWS Access Key Id you provided does not exist in our records")
    assert result is not None
    assert result.category == "credential"
    assert result.severity == Severity.CRITICAL


def test_classify_subnet_pattern():
    engine = _build_engine()
    result = engine.classify("Cannot allocate IP address in subnet subnet-0abc123def456. InsufficientFreeAddressesInSubnet")
    assert result is not None
    assert result.category == "network"


def test_classify_no_match():
    engine = _build_engine()
    result = engine.classify("Everything is fine, no errors here")
    assert result is None


def test_extract_subnet_id():
    engine = _build_engine()
    pattern = engine.classify("Cannot allocate IP address in subnet subnet-0abc123def456. InsufficientFreeAddressesInSubnet")
    assert pattern is not None
    extracted = engine.extract("Cannot allocate IP address in subnet subnet-0abc123def456. InsufficientFreeAddressesInSubnet", pattern)
    assert extracted.get("subnet_id") == "subnet-0abc123def456"


def test_from_config():
    config_list = [
        {
            "pattern": r"disk full",
            "severity": "critical",
            "message": "Disk full",
            "category": "storage",
        },
        {
            "pattern": r"warning.*threshold",
            "severity": "warning",
            "message": "Threshold warning",
            "category": "capacity",
        },
    ]
    engine = PatternEngine.from_config(config_list)
    result = engine.classify("disk full on volume D:")
    assert result is not None
    assert result.category == "storage"
    assert result.severity == Severity.CRITICAL

    result2 = engine.classify("warning: threshold exceeded")
    assert result2 is not None
    assert result2.category == "capacity"
    assert result2.severity == Severity.WARNING
