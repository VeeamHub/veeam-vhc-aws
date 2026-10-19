using VeeamVhcAws.Core.Config;
using VeeamVhcAws.Ui;
using Xunit;

namespace VeeamVhcAws.Tests.Ui;

/// <summary>
/// The Spectre setup wizard starts from <see cref="WizardAnswers"/> / <see cref="WizardServer"/>
/// defaults and hands them to <see cref="ConfigBuilder"/>. Pressing Enter through every prompt
/// must produce a config that verifies TLS, sends email over TLS, and matches the loader defaults.
/// </summary>
public class WizardDefaultsTests : IDisposable
{
    private readonly string _dir;
    private readonly IDisposable _key;

    public WizardDefaultsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vhc-wizard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _key = SecretKeyStore.UseKeyFile(Path.Combine(_dir, ".veeam-vhc-aws.key"));
    }

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void DefaultServer_VerifiesSsl()
    {
        var server = new WizardServer { Name = "s", Url = "https://vbr:9419", Username = "u", Password = "p" };

        var built = ServerDict(ConfigBuilder.Build(new WizardAnswers { Servers = { server } }));

        Assert.Equal("vbr", built["type"]);
        Assert.Equal(true, built["verify_ssl"]);
        Assert.Equal("1.3-rev1", built["api_version"]);
    }

    [Fact]
    public void Email_UsesTls_AndObfuscatesTheSmtpPassword()
    {
        var answers = new WizardAnswers
        {
            IncludeEmail = true,
            EmailSmtpHost = "smtp.example.com",
            EmailFrom = "vhc@example.com",
            EmailTo = "a@example.com, b@example.com",
            EmailSmtpUsername = "vhc",
            EmailSmtpPassword = "smtp-secret",
        };

        var outputs = (List<object>)ConfigBuilder.Build(answers)["output"];
        var email = Assert.Single(outputs.Cast<Dictionary<string, object>>(), o => (string)o["type"] == "email");

        Assert.Equal(true, email["use_tls"]);
        Assert.Equal(587, email["smtp_port"]);
        Assert.Equal(new List<string> { "a@example.com", "b@example.com" }, email["to_addrs"]);
        var stored = Assert.IsType<string>(email["smtp_password"]);
        Assert.StartsWith("ENC:", stored, StringComparison.Ordinal);
        Assert.Equal("smtp-secret", PasswordObfuscator.Deobfuscate(stored));
    }

    [Fact]
    public void DefaultAnswers_MatchTheLoaderDefaults()
    {
        var answers = new WizardAnswers();
        var empty = Path.Combine(_dir, "empty.yaml");
        File.WriteAllText(empty, "servers: []\n");
        var defaults = ConfigLoader.LoadConfig(empty);

        var repo = defaults.GetSection("repo_health").GetSection("thresholds");
        Assert.Equal(repo.Get("free_space_warning_pct", -1), answers.FreeSpaceWarningPct);
        Assert.Equal(repo.Get("free_space_critical_pct", -1), answers.FreeSpaceCriticalPct);

        var retention = defaults.GetSection("retention").GetSection("thresholds");
        Assert.Equal(retention.Get("overage_multiplier", -1.0), answers.OverageMultiplier);
        Assert.Equal(retention.Get("orphan_detection", false), answers.OrphanDetection);

        // RetentionMonitor falls back to 24h when session_lookback_hours is absent.
        Assert.Equal(24, answers.SessionLookbackHours);
    }

    [Fact]
    public void DefaultAnswers_WriteOnlyJsonStdout_AndEnableEveryMonitor()
    {
        var built = ConfigBuilder.Build(new WizardAnswers());

        var output = Assert.Single((List<object>)built["output"]);
        Assert.Equal("json_stdout", ((Dictionary<string, object>)output)["type"]);
        foreach (var monitor in new[] { "repo_health", "retention", "worker_health" })
            Assert.Equal(true, ((Dictionary<string, object>)built[monitor])["enabled"]);
        Assert.Equal(24, ((Dictionary<string, object>)built["worker_health"])["lookback_hours"]);
    }

    private static Dictionary<string, object> ServerDict(Dictionary<string, object> config) =>
        (Dictionary<string, object>)Assert.Single((List<object>)config["servers"]);
}
