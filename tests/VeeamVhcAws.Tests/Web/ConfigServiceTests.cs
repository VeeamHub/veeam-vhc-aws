using Xunit;
using VeeamVhcAws.Core.Config;
using VeeamVhcAws.Web;
using VeeamVhcAws.Web.Services;

namespace VeeamVhcAws.Tests.Web;

public class ConfigServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;

    public ConfigServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vhc-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config.yaml");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private ConfigService BuildService(string yaml)
    {
        File.WriteAllText(_configPath, yaml);
        var opts = new WebHostOptions(_configPath, "127.0.0.1", 9101);
        return new ConfigService(opts);
    }

    [Fact]
    public void Load_MissingFile_ReturnsConfigExistsFalse()
    {
        var opts = new WebHostOptions(_configPath, "127.0.0.1", 9101);
        var svc = new ConfigService(opts);
        var snap = svc.Load();
        Assert.False(snap.ConfigExists);
        Assert.Empty(snap.Servers);
    }

    [Fact]
    public void SaveServers_AddNewServer_PersistsObfuscatedPassword()
    {
        var svc = BuildService("servers: []\noutput:\n  - type: json_stdout\n");

        var newServer = new ServerEdit(
            Name: "new-vbr",
            Type: "vbr",
            Url: "https://vbr:9419",
            Username: "admin",
            Password: "PlainTextPw",
            PasswordChanged: true,
            VerifySsl: false,
            ApiVersion: "1.3-rev1");

        svc.SaveServers(new[] { newServer });

        var reloaded = ConfigLoader.LoadConfig(_configPath);
        var servers = reloaded.GetListOfSections("servers");
        Assert.Single(servers);
        var pw = servers[0].Get("password", "");
        Assert.StartsWith("ENC:", pw);
        Assert.Equal("PlainTextPw", PasswordObfuscator.Deobfuscate(pw));
    }

    [Fact]
    public void SaveServers_EditExisting_PasswordNotChanged_KeepsOriginal()
    {
        var initial = "servers:\n  - name: keep-pw\n    type: vbr\n    url: https://x:9419\n    username: u\n    password: \"ENC:originalciphertext\"\n    verify_ssl: false\n";
        var svc = BuildService(initial);

        var edit = new ServerEdit(
            Name: "keep-pw",
            Type: "vbr",
            Url: "https://x:9420",
            Username: "u",
            Password: "",
            PasswordChanged: false,
            VerifySsl: true,
            ApiVersion: "1.3-rev1");

        svc.SaveServers(new[] { edit });

        var reloaded = ConfigLoader.LoadConfig(_configPath);
        var server = reloaded.GetListOfSections("servers")[0];
        Assert.Equal("ENC:originalciphertext", server.Get("password", ""));
        Assert.Equal("https://x:9420", server.Get("url", ""));
        Assert.True(server.Get("verify_ssl", false));
    }

    [Fact]
    public void SaveServers_RemoveServer_PersistsRemoval()
    {
        var initial = "servers:\n  - name: a\n    type: vbr\n    url: https://a:9419\n    username: u\n    password: \"\"\n  - name: b\n    type: vbaws\n    url: https://b\n    username: u\n    password: \"\"\n";
        var svc = BuildService(initial);

        var remaining = new ServerEdit("a", "vbr", "https://a:9419", "u", "", false, false, "");
        svc.SaveServers(new[] { remaining });

        var reloaded = ConfigLoader.LoadConfig(_configPath);
        var servers = reloaded.GetListOfSections("servers");
        Assert.Single(servers);
        Assert.Equal("a", servers[0].Get("name", ""));
    }

    [Fact]
    public void SaveOutputs_AddsWebhook_PreservesFields()
    {
        var svc = BuildService("output:\n  - type: json_stdout\n");

        var newOutput = new OutputEdit(
            "webhook",
            new Dictionary<string, object?>
            {
                ["url"] = "https://ntfy.sh/topic",
                ["template"] = "ntfy",
                ["min_severity"] = "warning",
                ["deduplicate"] = true,
            },
            new HashSet<string>());

        svc.SaveOutputs(new[]
        {
            new OutputEdit("json_stdout", new(), new HashSet<string>()),
            newOutput,
        });

        var reloaded = ConfigLoader.LoadConfig(_configPath);
        var outputs = reloaded.GetListOfSections("output");
        Assert.Equal(2, outputs.Count);
        Assert.Equal("webhook", outputs[1].Get("type", ""));
        Assert.Equal("https://ntfy.sh/topic", outputs[1].Get("url", ""));
        Assert.Equal("ntfy", outputs[1].Get("template", ""));
    }

    [Fact]
    public void SaveOutputs_EmailWithUnchangedPassword_PreservesObfuscation()
    {
        var initial = "output:\n  - type: email\n    smtp_host: mail.example.com\n    smtp_port: 587\n    from_addr: a@b\n    to_addrs: c@d\n    smtp_username: user\n    smtp_password: \"ENC:originalcipher\"\n    use_tls: true\n";
        var svc = BuildService(initial);

        var edit = new OutputEdit(
            "email",
            new Dictionary<string, object?>
            {
                ["smtp_host"] = "mail.example.com",
                ["smtp_port"] = 587,
                ["from_addr"] = "a@b",
                ["to_addrs"] = "c@d",
                ["smtp_username"] = "user",
                ["smtp_password"] = "********",
                ["use_tls"] = true,
            },
            new HashSet<string>()); // no secret changed

        svc.SaveOutputs(new[] { edit });

        var reloaded = ConfigLoader.LoadConfig(_configPath);
        var output = reloaded.GetListOfSections("output")[0];
        Assert.Equal("ENC:originalcipher", output.Get("smtp_password", ""));
    }

    [Fact]
    public void SaveOutputs_EmailWithChangedPassword_ReobfuscatesNewValue()
    {
        var initial = "output:\n  - type: email\n    smtp_host: mail.example.com\n    smtp_port: 587\n    from_addr: a@b\n    to_addrs: c@d\n    smtp_username: user\n    smtp_password: \"ENC:originalcipher\"\n";
        var svc = BuildService(initial);

        var edit = new OutputEdit(
            "email",
            new Dictionary<string, object?>
            {
                ["smtp_host"] = "mail.example.com",
                ["smtp_port"] = 587,
                ["from_addr"] = "a@b",
                ["to_addrs"] = "c@d",
                ["smtp_username"] = "user",
                ["smtp_password"] = "NewPlaintext",
            },
            new HashSet<string> { "smtp_password" });

        svc.SaveOutputs(new[] { edit });

        var reloaded = ConfigLoader.LoadConfig(_configPath);
        var pw = reloaded.GetListOfSections("output")[0].Get("smtp_password", "");
        Assert.StartsWith("ENC:", pw);
        Assert.Equal("NewPlaintext", PasswordObfuscator.Deobfuscate(pw));
    }

    [Fact]
    public void SaveMonitorSection_UpdatesRepoHealthThresholds()
    {
        var svc = BuildService("repo_health:\n  enabled: true\n  thresholds:\n    free_space_warning_pct: 15\n");

        var section = new Dictionary<string, object>
        {
            ["enabled"] = true,
            ["thresholds"] = new Dictionary<string, object>
            {
                ["free_space_warning_pct"] = 20,
                ["free_space_critical_pct"] = 8,
            },
            ["session_lookback_hours"] = 48,
        };
        svc.SaveMonitorSection("repo_health", section);

        var reloaded = ConfigLoader.LoadConfig(_configPath);
        var repoHealth = reloaded.GetSection("repo_health");
        Assert.Equal(48, repoHealth.Get("session_lookback_hours", 0));
        var thresh = repoHealth.GetSection("thresholds");
        Assert.Equal(20, thresh.Get("free_space_warning_pct", 0));
        Assert.Equal(8, thresh.Get("free_space_critical_pct", 0));
    }

    [Fact]
    public void Load_PasswordMasked_NotExposedInSnapshot()
    {
        var initial = "servers:\n  - name: s\n    type: vbr\n    url: https://x:9419\n    username: u\n    password: \"ENC:secretvalue\"\n";
        var svc = BuildService(initial);

        var snap = svc.Load();
        Assert.Single(snap.Servers);
        // ServerView intentionally does NOT include the password
        var properties = typeof(ServerView).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("Password", properties);
    }
}
