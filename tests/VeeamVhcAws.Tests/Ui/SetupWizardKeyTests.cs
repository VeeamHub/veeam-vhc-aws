using VeeamVhcAws.Core.Config;
using VeeamVhcAws.Ui;
using Xunit;

namespace VeeamVhcAws.Tests.Ui;

/// <summary>
/// The setup wizard seals passwords as it writes the config. Every later command
/// (`all -c`, `test-connection -c`, `ui -c`) opens them with the key next to the config,
/// <c>&lt;config dir&gt;/.veeam-vhc-aws.key</c>. The wizard has to seal with that same key.
/// </summary>
public class SetupWizardKeyTests : IDisposable
{
    // Saving may bind the process-wide key directory to the config directory, as `all` and
    // `ui` do. Use one stable directory that outlives the test, like CommandWiringTests.
    private static readonly string ConfigDir = Path.Combine(Path.GetTempPath(), "vhc-wizard-save");

    // Stands in for the key the wizard resolves when nothing is bound (~/.vhc/secret.key, or
    // ProgramData on Windows), so the test never touches the real one.
    private readonly string _ambientDir = Path.Combine(Path.GetTempPath(), "vhc-wizard-ambient-" + Guid.NewGuid().ToString("N"));
    private readonly string _configPath;

    public SetupWizardKeyTests()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(_ambientDir);
        _configPath = Path.Combine(ConfigDir, Guid.NewGuid().ToString("N") + ".yaml");
    }

    public void Dispose()
    {
        File.Delete(_configPath);
        Directory.Delete(_ambientDir, recursive: true);
    }

    [Fact]
    public async Task SaveConfig_SealsPasswordsWithTheKeyALaterCommandReads()
    {
        var answers = new WizardAnswers
        {
            Servers = { new WizardServer { Name = "lab", Url = "https://vbr:9419", Username = "admin", Password = "wizard-pw" } },
        };

        // The wizard runs with no key directory bound to the config.
        SecretKeyStore.BindThreadDirectory(Path.Combine(_ambientDir, "unbound.yaml"));
        SetupWizard.SaveConfig(answers, _configPath);

        // A later command is a new process: nothing inherited, only the config directory bound.
        var password = await InFreshContext(() =>
        {
            SecretKeyStore.BindThreadDirectory(_configPath);
            var server = ConfigLoader.LoadConfig(_configPath).GetListOfSections("servers").Single();
            return PasswordObfuscator.Deobfuscate(server.Get("password", ""));
        });

        Assert.Equal("wizard-pw", password);
    }

    private static Task<T> InFreshContext<T>(Func<T> run)
    {
        using (ExecutionContext.SuppressFlow())
            return Task.Run(run);
    }
}
