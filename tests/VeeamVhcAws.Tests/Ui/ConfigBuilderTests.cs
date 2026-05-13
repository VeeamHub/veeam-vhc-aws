using Xunit;
using VeeamVhcAws.Ui;
using VeeamVhcAws.Core.Config;

namespace VeeamVhcAws.Tests.Ui;

/// <summary>
/// TDD tests for ConfigBuilder — wizard answers → YAML dict → round-trip through ConfigLoader.
/// </summary>
public class ConfigBuilderTests
{
    [Fact]
    public void Build_SingleVbrServer_ContainsServersKey()
    {
        var servers = new List<WizardServer>
        {
            new() { Name = "prod-vbr", Type = "vbr", Url = "https://vbr:9419", Username = "admin", Password = "secret", VerifySsl = false }
        };
        var answers = new WizardAnswers { Servers = servers };

        var dict = ConfigBuilder.Build(answers);

        Assert.True(dict.ContainsKey("servers"), "Built config must have 'servers' key");
    }

    [Fact]
    public void Build_SingleVbawsServer_TypeIsVbaws()
    {
        var servers = new List<WizardServer>
        {
            new() { Name = "prod-vbaws", Type = "vbaws", Url = "https://vbaws", Username = "admin", Password = "pw", VerifySsl = false }
        };
        var answers = new WizardAnswers { Servers = servers };

        var dict = ConfigBuilder.Build(answers);

        var serverList = dict["servers"] as List<object>;
        Assert.NotNull(serverList);
        Assert.Single(serverList);

        var server = serverList![0] as Dictionary<string, object>;
        Assert.NotNull(server);
        Assert.Equal("vbaws", server!["type"].ToString());
    }

    [Fact]
    public void Build_Password_IsObfuscated()
    {
        var plainPassword = "MyPlainPassword123";
        var servers = new List<WizardServer>
        {
            new() { Name = "server1", Type = "vbr", Url = "https://srv:9419", Username = "user", Password = plainPassword, VerifySsl = false }
        };
        var answers = new WizardAnswers { Servers = servers };

        var dict = ConfigBuilder.Build(answers);

        var serverList = dict["servers"] as List<object>;
        var server = serverList![0] as Dictionary<string, object>;
        var storedPassword = server!["password"].ToString()!;

        Assert.StartsWith("ENC:", storedPassword, StringComparison.Ordinal);
        Assert.NotEqual(plainPassword, storedPassword);

        // Must round-trip back to original
        var decrypted = PasswordObfuscator.Deobfuscate(storedPassword);
        Assert.Equal(plainPassword, decrypted);
    }

    [Fact]
    public void Build_EmptyPassword_IsNotObfuscated()
    {
        var servers = new List<WizardServer>
        {
            new() { Name = "server1", Type = "vbr", Url = "https://srv:9419", Username = "user", Password = "", VerifySsl = false }
        };
        var answers = new WizardAnswers { Servers = servers };

        var dict = ConfigBuilder.Build(answers);

        var serverList = dict["servers"] as List<object>;
        var server = serverList![0] as Dictionary<string, object>;
        var storedPassword = server!["password"].ToString()!;

        Assert.Equal("", storedPassword);
    }

    [Fact]
    public void Build_Thresholds_DefaultsApplied()
    {
        var answers = new WizardAnswers { Servers = new List<WizardServer>() };

        var dict = ConfigBuilder.Build(answers);

        Assert.True(dict.ContainsKey("repo_health"), "Must have repo_health section");
        var repoHealth = dict["repo_health"] as Dictionary<string, object>;
        Assert.NotNull(repoHealth);

        var thresholds = repoHealth!["thresholds"] as Dictionary<string, object>;
        Assert.NotNull(thresholds);

        // Default warning is 15, critical is 5
        Assert.Equal(15, Convert.ToInt32(thresholds!["free_space_warning_pct"]));
        Assert.Equal(5, Convert.ToInt32(thresholds["free_space_critical_pct"]));
    }

    [Fact]
    public void Build_CustomThresholds_Override()
    {
        var answers = new WizardAnswers
        {
            Servers = new List<WizardServer>(),
            FreeSpaceWarningPct = 20,
            FreeSpaceCriticalPct = 10,
        };

        var dict = ConfigBuilder.Build(answers);

        var repoHealth = dict["repo_health"] as Dictionary<string, object>;
        var thresholds = repoHealth!["thresholds"] as Dictionary<string, object>;

        Assert.Equal(20, Convert.ToInt32(thresholds!["free_space_warning_pct"]));
        Assert.Equal(10, Convert.ToInt32(thresholds["free_space_critical_pct"]));
    }

    [Fact]
    public void Build_OutputAlwaysContainsJsonStdout()
    {
        var answers = new WizardAnswers { Servers = new List<WizardServer>() };

        var dict = ConfigBuilder.Build(answers);

        Assert.True(dict.ContainsKey("output"), "Must have output key");
        var outputs = dict["output"] as List<object>;
        Assert.NotNull(outputs);
        Assert.True(outputs!.Count >= 1, "Must have at least one output handler");

        // First output must always be json_stdout (required)
        var first = outputs[0] as Dictionary<string, object>;
        Assert.Equal("json_stdout", first!["type"].ToString());
    }

    [Fact]
    public void Build_OrphanDetection_DefaultTrue()
    {
        var answers = new WizardAnswers { Servers = new List<WizardServer>() };

        var dict = ConfigBuilder.Build(answers);

        var retention = dict["retention"] as Dictionary<string, object>;
        Assert.NotNull(retention);
        var thresholds = retention!["thresholds"] as Dictionary<string, object>;
        Assert.NotNull(thresholds);

        var orphanDetection = Convert.ToBoolean(thresholds!["orphan_detection"]);
        Assert.True(orphanDetection);
    }

    [Fact]
    public void Build_RetentionLookback_DefaultIs24()
    {
        var answers = new WizardAnswers { Servers = new List<WizardServer>() };

        var dict = ConfigBuilder.Build(answers);

        var retention = dict["retention"] as Dictionary<string, object>;
        Assert.NotNull(retention);
        var lookback = Convert.ToInt32(retention!["session_lookback_hours"]);
        Assert.Equal(24, lookback);
    }

    [Fact]
    public void Build_MultipleServers_AllIncluded()
    {
        var servers = new List<WizardServer>
        {
            new() { Name = "vbr-1", Type = "vbr", Url = "https://vbr1:9419", Username = "u1", Password = "p1", VerifySsl = false },
            new() { Name = "vbr-2", Type = "vbr", Url = "https://vbr2:9419", Username = "u2", Password = "p2", VerifySsl = true },
            new() { Name = "vbaws-1", Type = "vbaws", Url = "https://vbaws1", Username = "u3", Password = "p3", VerifySsl = false },
        };
        var answers = new WizardAnswers { Servers = servers };

        var dict = ConfigBuilder.Build(answers);

        var serverList = dict["servers"] as List<object>;
        Assert.NotNull(serverList);
        Assert.Equal(3, serverList!.Count);
    }

    [Fact]
    public void Build_Server_VerifySsl_Preserved()
    {
        var servers = new List<WizardServer>
        {
            new() { Name = "s1", Type = "vbr", Url = "https://x:9419", Username = "u", Password = "p", VerifySsl = true },
            new() { Name = "s2", Type = "vbr", Url = "https://y:9419", Username = "u", Password = "p", VerifySsl = false },
        };
        var answers = new WizardAnswers { Servers = servers };

        var dict = ConfigBuilder.Build(answers);

        var serverList = dict["servers"] as List<object>;
        var s1 = serverList![0] as Dictionary<string, object>;
        var s2 = serverList[1] as Dictionary<string, object>;

        Assert.True(Convert.ToBoolean(s1!["verify_ssl"]));
        Assert.False(Convert.ToBoolean(s2!["verify_ssl"]));
    }

    [Fact]
    public void Build_ServerNames_ArePreserved()
    {
        var servers = new List<WizardServer>
        {
            new() { Name = "my-prod-vbr", Type = "vbr", Url = "https://vbr:9419", Username = "u", Password = "p", VerifySsl = false }
        };
        var answers = new WizardAnswers { Servers = servers };

        var dict = ConfigBuilder.Build(answers);

        var serverList = dict["servers"] as List<object>;
        var server = serverList![0] as Dictionary<string, object>;
        Assert.Equal("my-prod-vbr", server!["name"].ToString());
    }

    [Fact]
    public void Build_ToYaml_DoesNotThrow()
    {
        var servers = new List<WizardServer>
        {
            new() { Name = "test-vbr", Type = "vbr", Url = "https://vbr:9419", Username = "admin", Password = "secret", VerifySsl = false }
        };
        var answers = new WizardAnswers { Servers = servers };

        var dict = ConfigBuilder.Build(answers);

        // Should not throw
        var yaml = ConfigBuilder.ToYaml(dict);
        Assert.False(string.IsNullOrEmpty(yaml));
        Assert.Contains("test-vbr", yaml);
        Assert.Contains("servers", yaml);
    }

    [Fact]
    public void Build_ToYaml_RoundTripsViaConfigLoader()
    {
        var servers = new List<WizardServer>
        {
            new() { Name = "rt-server", Type = "vbr", Url = "https://vbr:9419", Username = "admin", Password = "secret123", VerifySsl = false }
        };
        var answers = new WizardAnswers { Servers = servers, FreeSpaceWarningPct = 20, FreeSpaceCriticalPct = 8 };

        var dict = ConfigBuilder.Build(answers);
        var yaml = ConfigBuilder.ToYaml(dict);

        // Write to a temp file and parse via ConfigLoader
        var tempFile = Path.GetTempFileName() + ".yaml";
        try
        {
            File.WriteAllText(tempFile, yaml);
            var loaded = ConfigLoader.LoadConfig(tempFile);

            Assert.True(loaded.ContainsKey("servers"));
            var serverList = loaded.GetListOfSections("servers");
            Assert.Single(serverList);
            Assert.Equal("rt-server", serverList[0].Get("name", ""));
            Assert.Equal("vbr", serverList[0].Get("type", ""));

            var repoHealth = loaded.GetSection("repo_health");
            var thresholds = repoHealth.GetSection("thresholds");
            Assert.Equal(20, thresholds.Get("free_space_warning_pct", 0));
            Assert.Equal(8, thresholds.Get("free_space_critical_pct", 0));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
