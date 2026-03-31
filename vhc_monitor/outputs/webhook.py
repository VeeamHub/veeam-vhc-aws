"""Webhook output handler with templates for Slack, Teams, PagerDuty, ntfy, and generic."""

import logging

import httpx

from vhc_monitor.core.output import OutputHandler
from vhc_monitor.core.models import MonitorResult, Severity

logger = logging.getLogger("vhc_monitor.outputs.webhook")


_SEVERITY_COLORS = {
    Severity.OK: "#36a64f",
    Severity.WARNING: "#ff9900",
    Severity.CRITICAL: "#ff0000",
    Severity.ERROR: "#cc0000",
}

_SEVERITY_ORDER = {
    Severity.OK: 0,
    Severity.WARNING: 1,
    Severity.CRITICAL: 2,
    Severity.ERROR: 3,
}


class WebhookHandler(OutputHandler):
    """POSTs monitor results to a webhook URL using a configurable template."""

    def __init__(
        self,
        url: str,
        template: str = "generic",
        min_severity: str = "ok",
        deduplicate: bool = True,
    ) -> None:
        self.url = url
        self.template = template.lower()
        self._min_severity = Severity(min_severity.lower())
        self._deduplicate = deduplicate

    def _should_send(self, results: list[MonitorResult]) -> bool:
        """Check if any result meets the minimum severity threshold."""
        min_order = _SEVERITY_ORDER[self._min_severity]
        for result in results:
            if _SEVERITY_ORDER.get(result.overall_severity, 0) >= min_order:
                return True
        return False

    def _format_slack(self, results: list[MonitorResult]) -> dict:
        """Format as Slack blocks with color-coded attachments."""
        attachments = []
        for result in results:
            color = _SEVERITY_COLORS.get(result.overall_severity, "#808080")
            finding_lines = []
            for f in result.findings:
                finding_lines.append(f"• [{f.severity.value.upper()}] {f.resource}: {f.message}")

            attachments.append({
                "color": color,
                "blocks": [
                    {
                        "type": "section",
                        "text": {
                            "type": "mrkdwn",
                            "text": (
                                f"*{result.monitor.value}* — "
                                f"{result.overall_severity.value.upper()}\n"
                                f"Duration: {result.duration_ms}ms\n"
                                + "\n".join(finding_lines)
                            ),
                        },
                    }
                ],
            })

        return {"attachments": attachments}

    def _format_teams(self, results: list[MonitorResult]) -> dict:
        """Format as Microsoft Teams MessageCard."""
        sections = []
        overall_color = "#808080"

        for result in results:
            overall_color = _SEVERITY_COLORS.get(result.overall_severity, overall_color)
            facts = [
                {"name": "Monitor", "value": result.monitor.value},
                {"name": "Severity", "value": result.overall_severity.value.upper()},
                {"name": "Duration", "value": f"{result.duration_ms}ms"},
                {"name": "Findings", "value": str(len(result.findings))},
            ]
            sections.append({
                "activityTitle": f"VHC Monitor: {result.monitor.value}",
                "facts": facts,
                "text": "\n".join(
                    f"- [{f.severity.value.upper()}] {f.resource}: {f.message}"
                    for f in result.findings
                ),
            })

        return {
            "@type": "MessageCard",
            "@context": "http://schema.org/extensions",
            "themeColor": overall_color.lstrip("#"),
            "summary": "VHC Monitor Results",
            "sections": sections,
        }

    def _format_pagerduty(self, results: list[MonitorResult]) -> dict:
        """Format as PagerDuty Events API v2 payload."""
        # Use the worst severity across all results
        worst = Severity.OK
        for result in results:
            if _SEVERITY_ORDER.get(result.overall_severity, 0) > _SEVERITY_ORDER.get(worst, 0):
                worst = result.overall_severity

        pd_severity_map = {
            Severity.OK: "info",
            Severity.WARNING: "warning",
            Severity.CRITICAL: "critical",
            Severity.ERROR: "error",
        }

        summary_parts = []
        for result in results:
            summary_parts.append(
                f"{result.monitor.value}: {result.overall_severity.value} "
                f"({len(result.findings)} findings)"
            )

        return {
            "routing_key": "",  # Must be set by user in config/URL
            "event_action": "trigger" if worst != Severity.OK else "resolve",
            "payload": {
                "summary": "; ".join(summary_parts),
                "severity": pd_severity_map.get(worst, "info"),
                "source": "vhc-monitor",
                "component": "veeam-backup",
                "custom_details": {
                    "results": [r.to_dict() for r in results],
                },
            },
        }

    def _format_generic(self, results: list[MonitorResult]) -> dict:
        """Format as raw JSON."""
        return {"results": [r.to_dict() for r in results]}

    def _format_ntfy(self, results: list[MonitorResult]) -> dict:
        """Format for ntfy.sh — returns a dict with _ntfy_headers for special handling."""
        worst = Severity.OK
        for result in results:
            if _SEVERITY_ORDER.get(result.overall_severity, 0) > _SEVERITY_ORDER.get(worst, 0):
                worst = result.overall_severity

        ntfy_priority = {
            Severity.OK: "low",
            Severity.WARNING: "default",
            Severity.CRITICAL: "high",
            Severity.ERROR: "urgent",
        }

        ntfy_tags = {
            Severity.OK: "white_check_mark",
            Severity.WARNING: "warning",
            Severity.CRITICAL: "rotating_light",
            Severity.ERROR: "x",
        }

        lines = []
        for result in results:
            # Skip monitors that are all OK -- no need to clutter the notification
            if result.overall_severity == Severity.OK:
                continue

            # Skip cross-correlation -- its findings duplicate what's already shown
            if result.monitor.value == "cross_correlation":
                continue

            server_label = f" ({result.server})" if result.server else ""
            non_metric_findings = [
                f for f in result.findings
                if not f.metric_name
            ]

            # Only show non-OK findings, skip already-reported if dedup enabled
            alertable = [
                f for f in non_metric_findings
                if f.severity != Severity.OK
                and not (self._deduplicate and f.details.get("_seen_before"))
            ]
            if not alertable:
                continue

            lines.append(f"**{result.monitor.value}{server_label}**")
            # Deduplicate: skip connection findings if they just repeat the errors list
            has_connection_findings = any("connection" in f.resource for f in alertable)
            for f in alertable[:15]:
                resource = f.resource
                if result.server and resource.startswith(f"[{result.server}] "):
                    resource = resource[len(f"[{result.server}] "):]
                lines.append(f"- {resource}: {f.message}")
            if len(alertable) > 15:
                lines.append(f"- ... and {len(alertable) - 15} more")

            # Only show raw errors if no connection findings already cover them
            if result.errors and not has_connection_findings:
                for e in result.errors[:3]:
                    lines.append(f"- ERROR: {e[:120]}")

            lines.append("")

        # Add a summary line for OK monitors so you know they ran
        ok_monitors = [
            r for r in results
            if r.overall_severity == Severity.OK
            and r.monitor.value != "cross_correlation"
        ]
        if ok_monitors:
            ok_summary = ", ".join(
                f"{r.monitor.value} ({r.server})" if r.server else r.monitor.value
                for r in ok_monitors
            )
            lines.append(f"OK: {ok_summary}")

        # Show resolved findings
        resolved = []
        for result in results:
            for f in result.findings:
                if f.message.startswith("RESOLVED:"):
                    resource = f.resource
                    if result.server and resource.startswith(f"[{result.server}] "):
                        resource = resource[len(f"[{result.server}] "):]
                    resolved.append(f"- {resource}: {f.message}")
        if resolved:
            lines.append("**Resolved:**")
            for r in resolved[:10]:
                lines.append(r)
            lines.append("")

        if not lines:
            lines.append("All monitors OK")

        body = "\n".join(lines).strip()
        # ntfy has a ~4KB body limit
        if len(body) > 3800:
            body = body[:3800] + "\n... (truncated)"

        return {
            "_ntfy_headers": {
                "Title": f"vhc-monitor: {worst.value.upper()}",
                "Priority": ntfy_priority.get(worst, "default"),
                "Tags": ntfy_tags.get(worst, "bell"),
                "Markdown": "yes",
            },
            "_ntfy_body": body,
        }

    def emit(self, results: list[MonitorResult]) -> None:
        """POST results to the webhook URL using the configured template."""
        if not self._should_send(results):
            return

        formatters = {
            "slack": self._format_slack,
            "teams": self._format_teams,
            "pagerduty": self._format_pagerduty,
            "ntfy": self._format_ntfy,
            "generic": self._format_generic,
        }

        formatter = formatters.get(self.template, self._format_generic)
        payload = formatter(results)

        # For ntfy with dedup: skip sending if there are no new alertable findings
        if self.template == "ntfy" and self._deduplicate:
            has_new_alerts = any(
                f.severity != Severity.OK
                and not f.metric_name
                and not f.details.get("_seen_before")
                and not f.message.startswith("RESOLVED:")
                for r in results for f in r.findings
            )
            has_resolved = any(
                f.message.startswith("RESOLVED:")
                for r in results for f in r.findings
            )
            if not has_new_alerts and not has_resolved:
                logger.debug("Skipping ntfy — no new or resolved findings")
                return

        try:
            with httpx.Client(timeout=30) as client:
                if self.template == "ntfy":
                    ntfy_headers = payload.pop("_ntfy_headers", {})
                    body = payload.pop("_ntfy_body", "")
                    response = client.post(self.url, content=body, headers=ntfy_headers)
                else:
                    response = client.post(self.url, json=payload)
                response.raise_for_status()
                logger.info("Webhook sent to %s (template=%s, status=%d)",
                            self.url, self.template, response.status_code)
        except Exception as e:
            logger.error("Webhook failed for %s: %s", self.url, e)
