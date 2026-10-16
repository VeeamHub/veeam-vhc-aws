using System.Text.Json;
using Xunit;
using VeeamVhcAws.Web;
using VeeamVhcAws.Web.Services;

namespace VeeamVhcAws.Tests.Web;

public class StateServiceAtomicTests
{
    [Fact]
    public async Task ConcurrentSuppress_KeepsEveryKey()
    {
        var dir = Directory.CreateTempSubdirectory("vhc-state-");
        try
        {
            var statePath = Path.Combine(dir.FullName, "state.json");
            var configPath = Path.Combine(dir.FullName, "veeam-vhc-aws.yaml");
            const int keys = 8;
            var findings = new Dictionary<string, object>();
            for (var i = 0; i < keys; i++)
            {
                findings[$"k{i}"] = new Dictionary<string, object>
                {
                    ["resource"] = $"repo:{i}",
                    ["message"] = "low",
                    ["server"] = "s",
                    ["monitor"] = "repo_health",
                };
            }
            File.WriteAllText(statePath, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["findings"] = findings,
                ["captured_errors"] = new Dictionary<string, object>(),
                ["suppressions"] = new Dictionary<string, object>(),
            }));
            File.WriteAllText(configPath, "global:\n  state_file: \"" + statePath.Replace("\\", "/") + "\"\n");

            var options = new WebHostOptions(configPath, "127.0.0.1", 9101);
            var tasks = Enumerable.Range(0, keys).Select(i => Task.Run(() =>
            {
                var service = new StateService(options);
                Assert.True(service.Suppress($"k{i}"));
            })).ToArray();
            await Task.WhenAll(tasks);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(statePath));
            Assert.Equal(keys, doc.RootElement.GetProperty("suppressions").EnumerateObject().Count());
            Assert.Equal(keys, doc.RootElement.GetProperty("findings").EnumerateObject().Count());
            Assert.True(File.Exists(statePath + ".lock"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
