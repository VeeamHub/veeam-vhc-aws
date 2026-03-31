from enum import Enum
from dataclasses import dataclass, field
from datetime import datetime
from typing import Optional


class Severity(Enum):
    OK = "ok"
    WARNING = "warning"
    CRITICAL = "critical"
    ERROR = "error"


class MonitorType(Enum):
    REPO_HEALTH = "repo_health"
    RETENTION = "retention"
    WORKER_HEALTH = "worker_health"
    CROSS_CORRELATION = "cross_correlation"


@dataclass
class Finding:
    severity: Severity
    resource: str
    message: str
    details: dict = field(default_factory=dict)
    metric_name: Optional[str] = None
    metric_value: Optional[float] = None


@dataclass
class MonitorResult:
    monitor: MonitorType
    timestamp: datetime
    duration_ms: int
    overall_severity: Severity
    findings: list[Finding]
    server: str = ""
    errors: list[str] = field(default_factory=list)
    metadata: dict = field(default_factory=dict)

    def to_dict(self) -> dict:
        result = {
            "monitor": self.monitor.value,
            "timestamp": self.timestamp.isoformat(),
            "duration_ms": self.duration_ms,
            "overall_severity": self.overall_severity.value,
            "findings": [
                {
                    "severity": f.severity.value,
                    "resource": f.resource,
                    "message": f.message,
                    "details": f.details,
                    **({"metric_name": f.metric_name} if f.metric_name else {}),
                    **({"metric_value": f.metric_value} if f.metric_value is not None else {}),
                }
                for f in self.findings
            ],
            "errors": self.errors,
            "metadata": self.metadata,
        }
        if self.server:
            result["server"] = self.server
        return result
