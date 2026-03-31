"""Email output handler for sending HTML monitor reports via SMTP."""

import smtplib
from email.mime.multipart import MIMEMultipart
from email.mime.text import MIMEText

from vhc_monitor.core.output import OutputHandler
from vhc_monitor.core.models import MonitorResult, Severity


_SEVERITY_ORDER = {
    Severity.OK: 0,
    Severity.WARNING: 1,
    Severity.CRITICAL: 2,
    Severity.ERROR: 3,
}

_SEVERITY_COLORS = {
    Severity.OK: "#36a64f",
    Severity.WARNING: "#ff9900",
    Severity.CRITICAL: "#ff0000",
    Severity.ERROR: "#cc0000",
}


class EmailHandler(OutputHandler):
    """Sends HTML email with monitor results via SMTP."""

    def __init__(
        self,
        smtp_host: str,
        smtp_port: int = 587,
        from_addr: str = "",
        to_addrs: list[str] | str = "",
        min_severity: str = "critical",
        smtp_username: str = "",
        smtp_password: str = "",
        use_tls: bool = True,
    ) -> None:
        self.smtp_host = smtp_host
        self.smtp_port = smtp_port
        self.from_addr = from_addr
        self.to_addrs = [to_addrs] if isinstance(to_addrs, str) else to_addrs
        self._min_severity = Severity(min_severity.lower())
        self.smtp_username = smtp_username
        self.smtp_password = smtp_password
        self.use_tls = use_tls

    def _should_send(self, results: list[MonitorResult]) -> bool:
        """Check if any result meets the minimum severity threshold."""
        min_order = _SEVERITY_ORDER[self._min_severity]
        for result in results:
            if _SEVERITY_ORDER.get(result.overall_severity, 0) >= min_order:
                return True
        return False

    def _build_html(self, results: list[MonitorResult]) -> str:
        """Build an HTML email body from monitor results."""
        rows = []
        for result in results:
            color = _SEVERITY_COLORS.get(result.overall_severity, "#808080")
            finding_html = ""
            for f in result.findings:
                f_color = _SEVERITY_COLORS.get(f.severity, "#808080")
                finding_html += (
                    f'<li><span style="color:{f_color};font-weight:bold;">'
                    f"[{f.severity.value.upper()}]</span> "
                    f"<strong>{f.resource}</strong>: {f.message}</li>"
                )

            rows.append(
                f"""
                <tr>
                    <td style="border:1px solid #ddd;padding:8px;">
                        <span style="color:{color};font-weight:bold;">
                            {result.overall_severity.value.upper()}
                        </span>
                    </td>
                    <td style="border:1px solid #ddd;padding:8px;">
                        {result.monitor.value}
                    </td>
                    <td style="border:1px solid #ddd;padding:8px;">
                        {result.duration_ms}ms
                    </td>
                    <td style="border:1px solid #ddd;padding:8px;">
                        <ul style="margin:0;padding-left:16px;">
                            {finding_html}
                        </ul>
                    </td>
                </tr>
                """
            )

        errors_section = ""
        all_errors = []
        for result in results:
            all_errors.extend(result.errors)
        if all_errors:
            error_items = "".join(f"<li>{e}</li>" for e in all_errors)
            errors_section = f"""
            <h3 style="color:#cc0000;">Errors</h3>
            <ul>{error_items}</ul>
            """

        return f"""
        <html>
        <body style="font-family:Arial,sans-serif;">
            <h2>VHC Monitor Report</h2>
            <table style="border-collapse:collapse;width:100%;">
                <tr style="background:#f2f2f2;">
                    <th style="border:1px solid #ddd;padding:8px;text-align:left;">Severity</th>
                    <th style="border:1px solid #ddd;padding:8px;text-align:left;">Monitor</th>
                    <th style="border:1px solid #ddd;padding:8px;text-align:left;">Duration</th>
                    <th style="border:1px solid #ddd;padding:8px;text-align:left;">Findings</th>
                </tr>
                {"".join(rows)}
            </table>
            {errors_section}
        </body>
        </html>
        """

    def emit(self, results: list[MonitorResult]) -> None:
        """Send HTML email with monitor results if severity threshold is met."""
        if not self._should_send(results):
            return

        html = self._build_html(results)

        msg = MIMEMultipart("alternative")
        msg["Subject"] = "VHC Monitor Alert"
        msg["From"] = self.from_addr
        msg["To"] = ", ".join(self.to_addrs)

        msg.attach(MIMEText(html, "html"))

        with smtplib.SMTP(self.smtp_host, self.smtp_port) as server:
            if self.use_tls:
                server.starttls()
            if self.smtp_username:
                server.login(self.smtp_username, self.smtp_password)
            server.sendmail(self.from_addr, self.to_addrs, msg.as_string())
