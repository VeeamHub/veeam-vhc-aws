using System.Text;
using MailKit.Net.Smtp;
using MimeKit;
using Serilog;
using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Core.Output;

namespace VeeamVhcAws.Outputs;

public class EmailHandler : IOutputHandler
{
    private static readonly ILogger Logger = Log.ForContext<EmailHandler>();

    private static readonly Dictionary<Severity, string> SeverityColors = new()
    {
        [Severity.Ok] = "#36a64f",
        [Severity.Warning] = "#ff9900",
        [Severity.Critical] = "#ff0000",
        [Severity.Error] = "#cc0000",
    };

    private readonly string _smtpHost;
    private readonly int _smtpPort;
    private readonly string _fromAddr;
    private readonly List<string> _toAddrs;
    private readonly Severity _minSeverity;
    private readonly string _smtpUsername;
    private readonly string _smtpPassword;
    private readonly bool _useTls;

    public EmailHandler(string smtpHost, int smtpPort = 587,
        string fromAddr = "", List<string>? toAddrs = null,
        string minSeverity = "critical",
        string smtpUsername = "", string smtpPassword = "", bool useTls = true)
    {
        _smtpHost = smtpHost;
        _smtpPort = smtpPort;
        _fromAddr = fromAddr;
        _toAddrs = toAddrs ?? new List<string>();
        _minSeverity = SeverityExtensions.ParseSeverity(minSeverity);
        _smtpUsername = smtpUsername;
        _smtpPassword = smtpPassword;
        _useTls = useTls;
    }

    private bool ShouldSend(IReadOnlyList<MonitorResult> results)
    {
        var minOrder = _minSeverity.Rank();
        return results.Any(r => r.OverallSeverity.Rank() >= minOrder);
    }

    private static string BoldWorkloadsHtml(string message)
    {
        var idx = message.IndexOf("Workloads:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return message;
        var prefix = message[..(idx + "Workloads:".Length)];
        var list = message[(idx + "Workloads:".Length)..].Trim();
        return $"{prefix} <strong>{list}</strong>";
    }

    private string BuildHtml(IReadOnlyList<MonitorResult> results)
    {
        var rows = new StringBuilder();
        foreach (var result in results)
        {
            var nonOkFindings = result.Findings.Where(f => f.Severity != Severity.Ok).ToList();
            if (nonOkFindings.Count == 0) continue;

            var color = SeverityColors.GetValueOrDefault(result.OverallSeverity, "#808080");
            var findingHtml = new StringBuilder();
            foreach (var f in nonOkFindings)
            {
                var fColor = SeverityColors.GetValueOrDefault(f.Severity, "#808080");
                findingHtml.Append(
                    $"<li><span style=\"color:{fColor};font-weight:bold;\">" +
                    $"[{f.Severity.ToLowerString().ToUpperInvariant()}]</span> " +
                    $"<strong>{f.Resource}</strong>: {BoldWorkloadsHtml(f.Message)}</li>");
            }

            rows.Append($@"
                <tr>
                    <td style=""border:1px solid #ddd;padding:8px;"">
                        <span style=""color:{color};font-weight:bold;"">
                            {result.OverallSeverity.ToLowerString().ToUpperInvariant()}
                        </span>
                    </td>
                    <td style=""border:1px solid #ddd;padding:8px;"">
                        {result.Monitor.ToLowerString()}
                    </td>
                    <td style=""border:1px solid #ddd;padding:8px;"">
                        {result.DurationMs}ms
                    </td>
                    <td style=""border:1px solid #ddd;padding:8px;"">
                        <ul style=""margin:0;padding-left:16px;"">
                            {findingHtml}
                        </ul>
                    </td>
                </tr>");
        }

        var errorsSection = "";
        var allErrors = results.SelectMany(r => r.Errors).ToList();
        if (allErrors.Count > 0)
        {
            var errorItems = string.Join("", allErrors.Select(e => $"<li>{e}</li>"));
            errorsSection = $@"
            <h3 style=""color:#cc0000;"">Errors</h3>
            <ul>{errorItems}</ul>";
        }

        return $@"
        <html>
        <body style=""font-family:Arial,sans-serif;"">
            <h2>Veeam VHC AWS Report</h2>
            <table style=""border-collapse:collapse;width:100%;"">
                <tr style=""background:#f2f2f2;"">
                    <th style=""border:1px solid #ddd;padding:8px;text-align:left;"">Severity</th>
                    <th style=""border:1px solid #ddd;padding:8px;text-align:left;"">Monitor</th>
                    <th style=""border:1px solid #ddd;padding:8px;text-align:left;"">Duration</th>
                    <th style=""border:1px solid #ddd;padding:8px;text-align:left;"">Findings</th>
                </tr>
                {rows}
            </table>
            {errorsSection}
        </body>
        </html>";
    }

    /// <summary>
    /// Decides whether an email should be sent for these results.
    /// A daily summary (Metadata["summary"] == true) always sends — even when everything is
    /// healthy — because an "all clear" report is the point of a daily summary. Alert (non-summary)
    /// runs are gated by both the minimum-severity threshold and the presence of findings/errors.
    /// </summary>
    public bool ShouldEmit(IReadOnlyList<MonitorResult> results, out string? skipReason)
    {
        var isSummary = results.Any(r => r.Metadata.ContainsKey("summary") && r.Metadata["summary"] is true);

        if (!isSummary && !ShouldSend(results))
        {
            skipReason = $"severity below threshold ({_minSeverity})";
            return false;
        }

        // A summary always sends — an "all clear" daily report is the point of a summary.
        // Only alert (non-summary) runs are suppressed when there is nothing to report.
        var hasContent = results.Any(r => r.Findings.Any(f => f.Severity != Severity.Ok))
                         || results.Any(r => r.Errors.Any());
        if (!isSummary && !hasContent)
        {
            skipReason = "no findings or errors to report";
            return false;
        }

        skipReason = null;
        return true;
    }

    public void Emit(IReadOnlyList<MonitorResult> results)
    {
        if (!ShouldEmit(results, out var skipReason))
        {
            Logger.Information("Email skipped — {SkipReason}", skipReason);
            return;
        }

        var isSummary = results.Any(r => r.Metadata.ContainsKey("summary") && r.Metadata["summary"] is true);
        var html = BuildHtml(results);

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_fromAddr));
        foreach (var addr in _toAddrs)
            message.To.Add(MailboxAddress.Parse(addr));
        message.Subject = isSummary ? "Daily Veeam VHC AWS Summary" : "Veeam VHC AWS Alert";

        var bodyBuilder = new BodyBuilder { HtmlBody = html };
        message.Body = bodyBuilder.ToMessageBody();

        try
        {
            using var client = new SmtpClient();
            client.Connect(_smtpHost, _smtpPort, _useTls ? MailKit.Security.SecureSocketOptions.StartTls : MailKit.Security.SecureSocketOptions.None);
            if (!string.IsNullOrEmpty(_smtpUsername))
                client.Authenticate(_smtpUsername, _smtpPassword);
            client.Send(message);
            client.Disconnect(true);

            Logger.Information("Email sent to {Recipients}", string.Join(", ", _toAddrs));
        }
        catch (Exception e)
        {
            Logger.Error("Email failed: {Error}", e.Message);
        }
    }
}
