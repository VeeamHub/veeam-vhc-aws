using Prometheus;
using Serilog;
using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Core.Output;

namespace VeeamVhcAws.Outputs;

public class PrometheusHandler : IOutputHandler
{
    private static readonly ILogger Logger = Log.ForContext<PrometheusHandler>();

    private static readonly Dictionary<Severity, double> SeverityValues = new()
    {
        [Severity.Ok] = 0, [Severity.Warning] = 1,
        [Severity.Critical] = 2, [Severity.Error] = 3,
    };

    private readonly string _mode;
    private readonly string _url;
    private readonly string _job;
    private readonly int _port;
    private readonly bool _allowLoopback;
    private readonly CollectorRegistry _registry;
    private bool _serverStarted;
    private readonly List<string> _logs = new();

    internal IReadOnlyList<string> Logs => _logs;

    private readonly Gauge _overallSeverity;
    private readonly Gauge _findingCount;
    private readonly Gauge _durationMs;
    private readonly Gauge _findingMetric;

    public PrometheusHandler(string mode = "pushgateway", string? url = null,
        string job = "veeam_vhc_aws", int port = 9100, bool allowLoopback = false)
    {
        _mode = mode.ToLowerInvariant();
        _url = url ?? "localhost:9091";
        _job = job;
        _port = port;
        _allowLoopback = allowLoopback;
        _registry = Metrics.NewCustomRegistry();

        var factory = Metrics.WithCustomRegistry(_registry);
        _overallSeverity = factory.CreateGauge("veeam_vhc_aws_overall_severity",
            "Overall severity of monitor run (0=ok, 1=warning, 2=critical, 3=error)",
            new GaugeConfiguration { LabelNames = new[] { "monitor" } });
        _findingCount = factory.CreateGauge("veeam_vhc_aws_finding_count",
            "Number of findings from monitor run",
            new GaugeConfiguration { LabelNames = new[] { "monitor", "severity" } });
        _durationMs = factory.CreateGauge("veeam_vhc_aws_duration_ms",
            "Duration of monitor run in milliseconds",
            new GaugeConfiguration { LabelNames = new[] { "monitor" } });
        _findingMetric = factory.CreateGauge("veeam_vhc_aws_finding_value",
            "Value from a specific finding metric",
            new GaugeConfiguration { LabelNames = new[] { "monitor", "metric_name", "resource" } });
    }

    public void Emit(IReadOnlyList<MonitorResult> results)
    {
        foreach (var result in results)
        {
            var monitorName = result.Monitor.ToLowerString();

            _overallSeverity.WithLabels(monitorName).Set(SeverityValues.GetValueOrDefault(result.OverallSeverity, 0));
            _durationMs.WithLabels(monitorName).Set(result.DurationMs);

            var severityCounts = new Dictionary<string, int>();
            foreach (var finding in result.Findings)
            {
                var sev = finding.Severity.ToLowerString();
                severityCounts[sev] = severityCounts.GetValueOrDefault(sev, 0) + 1;

                if (finding.MetricName != null && finding.MetricValue.HasValue)
                {
                    _findingMetric.WithLabels(monitorName, finding.MetricName, finding.Resource)
                        .Set(finding.MetricValue.Value);
                }
            }

            foreach (var (sev, count) in severityCounts)
                _findingCount.WithLabels(monitorName, sev).Set(count);
        }

        if (_mode == "pushgateway")
        {
            var endpoint = _url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? _url : $"http://{_url}";
            var decision = OutboundUrlDecision.Decide(endpoint, _allowLoopback);
            if (!decision.Allowed)
            {
                Record($"Pushgateway rejected for {decision.Host}: {decision.Reason}");
                return;
            }

            try
            {
                using var client = OutboundUrlDecision.CreateClient();
                var pusher = new MetricPusher(new MetricPusherOptions
                {
                    Endpoint = endpoint,
                    Job = _job,
                    Registry = _registry,
                    HttpClientProvider = () => client,
                });
                pusher.Start();
                Thread.Sleep(500);
                pusher.Stop();
            }
            catch (Exception e)
            {
                Record($"Pushgateway failed for {decision.Host}: {e.Message}");
            }
        }
        else if (_mode == "server" && !_serverStarted)
        {
            var server = new MetricServer(hostname: "127.0.0.1", port: _port, registry: _registry);
            server.Start();
            _serverStarted = true;
            Logger.Information("Prometheus metric server started on port {Port}", _port);
        }
    }

    private void Record(string message)
    {
        _logs.Add(message);
        Logger.Error("{PushgatewayLog}", message);
    }
}
