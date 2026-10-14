using System.CommandLine;
using System.Reflection;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;
using VeeamVhcAws.Commands;
using VeeamVhcAws.Core.Config;
using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Core.Output;
using VeeamVhcAws.Outputs;

namespace VeeamVhcAws.Tests.Outputs;

[Collection("console")]
public class DeliveryFailureTests
{
    [Fact]
    public void Emit_DiscardedStdout_WarnsWhenItIsTheOnlyHandler()
    {
        var previousOut = Console.Out;
        var previousLog = Log.Logger;
        Console.SetOut(TextWriter.Null);
        var sink = new CaptureSink();
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        try
        {
            new JsonStdoutHandler { WarnWhenDiscarded = true }.Emit(new[] { Sample() });
            Assert.Contains(sink.Messages, message => message.Contains("discards stdout"));
        }
        finally
        {
            Console.SetOut(previousOut);
            Log.Logger = previousLog;
        }
    }

    [Fact]
    public void Emit_DiscardedStdout_StaysQuietWhenAnotherHandlerExists()
    {
        var previousOut = Console.Out;
        var previousLog = Log.Logger;
        var sink = new CaptureSink();
        Console.SetOut(TextWriter.Null);
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        try
        {
            new JsonStdoutHandler { WarnWhenDiscarded = false }.Emit(new[] { Sample() });
            Assert.DoesNotContain(sink.Messages, message => message.Contains("discards stdout"));
        }
        finally
        {
            Console.SetOut(previousOut);
            Log.Logger = previousLog;
        }
    }

    [Fact]
    public void Emit_BadEmailAddress_IsLoggedAndDoesNotThrow()
    {
        var previousLog = Log.Logger;
        var sink = new CaptureSink();
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        try
        {
            var handler = new EmailHandler("127.0.0.1", 1, "not an address", new List<string> { "also bad" }, "ok");
            var ex = Record.Exception(() => handler.Emit(new[] { Sample() }));
            Assert.Null(ex);
            Assert.Contains(sink.Messages, message => message.Contains("Email failed"));
        }
        finally
        {
            Log.Logger = previousLog;
        }
    }

    [Fact]
    public void LoadConfig_LegacyEmailKeys_ReachTheHandler()
    {
        var dir = Directory.CreateTempSubdirectory("vhc-mail-");
        var path = Path.Combine(dir.FullName, "veeam-vhc-aws.yaml");
        File.WriteAllText(path, """
            output:
              - type: email
                smtp_host: smtp.example
                from: vhc@example
                to: ops@example
            """);
        try
        {
            var loaded = ConfigLoader.LoadConfig(path);
            var email = loaded.GetListOfSections("output").Single(section => section.Get("type", "") == "email");
            Assert.Equal("vhc@example", email.Get("from_addr", ""));
            Assert.Equal("ops@example", email.Get("to_addrs", ""));
            Assert.False(email.ContainsKey("from"));
            Assert.False(email.ContainsKey("to"));

            var handler = Assert.IsType<EmailHandler>(OutputHandlerFactory.CreateHandlers(loaded, path).Single(h => h is EmailHandler));
            var from = (string)typeof(EmailHandler).GetField("_fromAddr", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handler)!;
            var to = (List<string>)typeof(EmailHandler).GetField("_toAddrs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handler)!;
            Assert.Equal("vhc@example", from);
            Assert.Equal(new[] { "ops@example" }, to);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task EncryptConfig_RewritesLegacyEmailKeys()
    {
        var dir = Directory.CreateTempSubdirectory("vhc-mail-migrate-");
        var path = Path.Combine(dir.FullName, "veeam-vhc-aws.yaml");
        await File.WriteAllTextAsync(path, """
            output:
              - type: email
                from: vhc@example
                to: ops@example
            """);
        try
        {
            var command = EncryptConfigCommand.Create();
            var exit = await command.InvokeAsync(new[] { "--config", path });
            Assert.Equal(0, exit);
            var updated = await File.ReadAllTextAsync(path);
            Assert.Contains("from_addr: vhc@example", updated);
            Assert.Contains("to_addrs: ops@example", updated);
            Assert.DoesNotContain("\n    from:", updated);
            Assert.DoesNotContain("\n    to:", updated);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private sealed class CaptureSink : ILogEventSink
    {
        public List<string> Messages { get; } = new();
        public void Emit(LogEvent logEvent) => Messages.Add(logEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static MonitorResult Sample() =>
        new(MonitorType.RepoHealth, DateTime.UtcNow, 10, Severity.Critical,
            new List<Finding> { new(Severity.Critical, "repo:demo", "Disk full") }, server: "s1");
}
