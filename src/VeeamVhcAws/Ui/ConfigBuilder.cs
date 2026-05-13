using VeeamVhcAws.Core.Config;
using YamlDotNet.Serialization;

namespace VeeamVhcAws.Ui;

/// <summary>
/// Data model for a single server collected by the setup wizard.
/// </summary>
public class WizardServer
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "vbr"; // "vbr" or "vbaws"
    public string Url { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool VerifySsl { get; set; } = false;
}

/// <summary>
/// Complete set of answers collected by SetupWizard.
/// </summary>
public class WizardAnswers
{
    public List<WizardServer> Servers { get; set; } = new();

    // Output channel selections
    public bool IncludeJsonFile { get; set; } = false;
    public bool IncludePrometheus { get; set; } = false;

    // Notification channels
    public bool IncludeSlack { get; set; } = false;
    public string SlackWebhookUrl { get; set; } = "";
    public bool IncludeTeams { get; set; } = false;
    public string TeamsWebhookUrl { get; set; } = "";
    public bool IncludeEmail { get; set; } = false;
    public string EmailSmtpHost { get; set; } = "";
    public int EmailSmtpPort { get; set; } = 587;
    public string EmailFrom { get; set; } = "";
    public string EmailTo { get; set; } = "";
    public string EmailSmtpUsername { get; set; } = "";
    public string EmailSmtpPassword { get; set; } = "";
    public bool IncludeNtfy { get; set; } = false;
    public string NtfyUrl { get; set; } = "";

    // Threshold overrides (wizard defaults match config defaults)
    public int FreeSpaceWarningPct { get; set; } = 15;
    public int FreeSpaceCriticalPct { get; set; } = 5;
    public double OverageMultiplier { get; set; } = 1.5;
    public bool OrphanDetection { get; set; } = true;
    public int SessionLookbackHours { get; set; } = 24;
}

/// <summary>
/// Converts WizardAnswers into a Dictionary that matches the veeam-vhc-aws YAML schema,
/// then serializes to YAML string. Passwords are obfuscated before storage.
/// This class is pure data manipulation — no UI concerns.
/// </summary>
public static class ConfigBuilder
{
    /// <summary>
    /// Build config dictionary from wizard answers.
    /// All passwords are obfuscated via PasswordObfuscator.Obfuscate().
    /// </summary>
    public static Dictionary<string, object> Build(WizardAnswers answers)
    {
        var config = new Dictionary<string, object>();

        // Servers
        var servers = new List<object>();
        foreach (var s in answers.Servers)
        {
            var serverDict = new Dictionary<string, object>
            {
                ["name"] = s.Name,
                ["type"] = s.Type,
                ["url"] = s.Url,
                ["username"] = s.Username,
                ["password"] = PasswordObfuscator.Obfuscate(s.Password),
                ["verify_ssl"] = s.VerifySsl,
            };

            // VBR needs api_version
            if (s.Type == "vbr")
                serverDict["api_version"] = "1.3-rev1";

            servers.Add(serverDict);
        }
        config["servers"] = servers;

        // Output handlers — json_stdout is always first and required
        var outputs = new List<object>
        {
            new Dictionary<string, object> { ["type"] = "json_stdout" }
        };

        if (answers.IncludeJsonFile)
            outputs.Add(new Dictionary<string, object> { ["type"] = "json_file", ["path"] = "./veeam-vhc-aws-output.json" });

        if (answers.IncludePrometheus)
            outputs.Add(new Dictionary<string, object> { ["type"] = "prometheus", ["port"] = 9101 });

        if (answers.IncludeSlack && !string.IsNullOrEmpty(answers.SlackWebhookUrl))
            outputs.Add(new Dictionary<string, object> { ["type"] = "webhook", ["url"] = answers.SlackWebhookUrl, ["format"] = "slack" });

        if (answers.IncludeTeams && !string.IsNullOrEmpty(answers.TeamsWebhookUrl))
            outputs.Add(new Dictionary<string, object> { ["type"] = "webhook", ["url"] = answers.TeamsWebhookUrl, ["format"] = "teams" });

        if (answers.IncludeNtfy && !string.IsNullOrEmpty(answers.NtfyUrl))
            outputs.Add(new Dictionary<string, object> { ["type"] = "webhook", ["url"] = answers.NtfyUrl, ["format"] = "ntfy" });

        if (answers.IncludeEmail && !string.IsNullOrEmpty(answers.EmailSmtpHost))
        {
            var emailOutput = new Dictionary<string, object>
            {
                ["type"] = "email",
                ["smtp_host"] = answers.EmailSmtpHost,
                ["smtp_port"] = answers.EmailSmtpPort,
                ["from"] = answers.EmailFrom,
                ["to"] = answers.EmailTo,
                ["use_tls"] = false,
            };
            if (!string.IsNullOrEmpty(answers.EmailSmtpUsername))
            {
                emailOutput["smtp_username"] = answers.EmailSmtpUsername;
                emailOutput["smtp_password"] = PasswordObfuscator.Obfuscate(answers.EmailSmtpPassword);
            }
            outputs.Add(emailOutput);
        }

        config["output"] = outputs;

        // Repo health
        config["repo_health"] = new Dictionary<string, object>
        {
            ["enabled"] = true,
            ["thresholds"] = new Dictionary<string, object>
            {
                ["free_space_warning_pct"] = answers.FreeSpaceWarningPct,
                ["free_space_critical_pct"] = answers.FreeSpaceCriticalPct,
            },
        };

        // Retention
        config["retention"] = new Dictionary<string, object>
        {
            ["enabled"] = true,
            ["session_lookback_hours"] = answers.SessionLookbackHours,
            ["thresholds"] = new Dictionary<string, object>
            {
                ["overage_multiplier"] = answers.OverageMultiplier,
                ["orphan_detection"] = answers.OrphanDetection,
            },
        };

        // Worker health
        config["worker_health"] = new Dictionary<string, object>
        {
            ["enabled"] = true,
            ["lookback_hours"] = 24,
        };

        return config;
    }

    /// <summary>
    /// Serialize a config dictionary to YAML string using snake_case conventions.
    /// </summary>
    public static string ToYaml(Dictionary<string, object> config)
    {
        var serializer = new SerializerBuilder().Build();

        return serializer.Serialize(config);
    }
}
