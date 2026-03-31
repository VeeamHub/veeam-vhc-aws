"""Prometheus output handler for pushgateway and HTTP server modes."""

from prometheus_client import Gauge, push_to_gateway, start_http_server, CollectorRegistry

from vhc_monitor.core.output import OutputHandler
from vhc_monitor.core.models import MonitorResult, Severity


_SEVERITY_VALUES = {
    Severity.OK: 0,
    Severity.WARNING: 1,
    Severity.CRITICAL: 2,
    Severity.ERROR: 3,
}


class PrometheusHandler(OutputHandler):
    """Exposes monitor results as Prometheus metrics."""

    def __init__(
        self,
        mode: str = "pushgateway",
        url: str | None = None,
        job: str = "vhc_monitor",
        port: int = 9100,
    ) -> None:
        self.mode = mode.lower()
        self.url = url or "localhost:9091"
        self.job = job
        self.port = port
        self._registry = CollectorRegistry()
        self._server_started = False

        # Create gauges
        self._overall_severity = Gauge(
            "vhc_monitor_overall_severity",
            "Overall severity of monitor run (0=ok, 1=warning, 2=critical, 3=error)",
            ["monitor"],
            registry=self._registry,
        )
        self._finding_count = Gauge(
            "vhc_monitor_finding_count",
            "Number of findings from monitor run",
            ["monitor", "severity"],
            registry=self._registry,
        )
        self._duration_ms = Gauge(
            "vhc_monitor_duration_ms",
            "Duration of monitor run in milliseconds",
            ["monitor"],
            registry=self._registry,
        )
        self._finding_metric = Gauge(
            "vhc_monitor_finding_value",
            "Value from a specific finding metric",
            ["monitor", "metric_name", "resource"],
            registry=self._registry,
        )

    def emit(self, results: list[MonitorResult]) -> None:
        """Push or expose metrics from monitor results."""
        for result in results:
            monitor_name = result.monitor.value

            # Overall severity
            self._overall_severity.labels(monitor=monitor_name).set(
                _SEVERITY_VALUES.get(result.overall_severity, 0)
            )

            # Duration
            self._duration_ms.labels(monitor=monitor_name).set(result.duration_ms)

            # Finding counts by severity
            severity_counts: dict[str, int] = {}
            for finding in result.findings:
                sev = finding.severity.value
                severity_counts[sev] = severity_counts.get(sev, 0) + 1

                # Individual finding metrics
                if finding.metric_name and finding.metric_value is not None:
                    self._finding_metric.labels(
                        monitor=monitor_name,
                        metric_name=finding.metric_name,
                        resource=finding.resource,
                    ).set(finding.metric_value)

            for sev, count in severity_counts.items():
                self._finding_count.labels(
                    monitor=monitor_name, severity=sev
                ).set(count)

        if self.mode == "pushgateway":
            push_to_gateway(self.url, job=self.job, registry=self._registry)
        elif self.mode == "server" and not self._server_started:
            start_http_server(self.port, registry=self._registry)
            self._server_started = True
