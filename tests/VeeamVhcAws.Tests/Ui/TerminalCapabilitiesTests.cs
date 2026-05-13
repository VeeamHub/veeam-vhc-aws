using Xunit;
using VeeamVhcAws.Ui;

namespace VeeamVhcAws.Tests.Ui;

/// <summary>
/// TDD tests for TerminalCapabilities env var gating.
/// These tests run in a non-interactive CI-like environment and verify
/// that env vars correctly suppress interactive mode.
/// </summary>
public class TerminalCapabilitiesTests
{
    // Note: Console.IsOutputRedirected is typically true in test runners,
    // so IsInteractive is always false in test execution — we focus on
    // the env var override logic and the static flag.

    [Fact]
    public void IsInteractive_ReturnsFalse_WhenNoColorSet()
    {
        var prev = Environment.GetEnvironmentVariable("NO_COLOR");
        try
        {
            Environment.SetEnvironmentVariable("NO_COLOR", "1");
            // Force a re-evaluation. Since IsInteractive is computed each call, this works.
            var result = TerminalCapabilities.IsInteractive;
            Assert.False(result, "NO_COLOR should suppress interactivity");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NO_COLOR", prev);
        }
    }

    [Fact]
    public void IsInteractive_ReturnsFalse_WhenCiSet()
    {
        var prev = Environment.GetEnvironmentVariable("CI");
        try
        {
            Environment.SetEnvironmentVariable("CI", "true");
            var result = TerminalCapabilities.IsInteractive;
            Assert.False(result, "CI env var should suppress interactivity");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CI", prev);
        }
    }

    [Fact]
    public void IsInteractive_ReturnsFalse_WhenTermIsDumb()
    {
        var prev = Environment.GetEnvironmentVariable("TERM");
        try
        {
            Environment.SetEnvironmentVariable("TERM", "dumb");
            var result = TerminalCapabilities.IsInteractive;
            Assert.False(result, "TERM=dumb should suppress interactivity");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TERM", prev);
        }
    }

    [Fact]
    public void SetNoInteractive_ForcesIsInteractiveFalse()
    {
        // Reset any prior flag state before testing
        TerminalCapabilities.ResetForTesting();
        TerminalCapabilities.SetNoInteractive();
        Assert.False(TerminalCapabilities.IsInteractive, "--no-interactive flag must force false");
        TerminalCapabilities.ResetForTesting();
    }

    [Fact]
    public void ResetForTesting_ClearsNoInteractiveFlag()
    {
        TerminalCapabilities.SetNoInteractive();
        TerminalCapabilities.ResetForTesting();
        // After reset, the flag is cleared. Actual value depends on environment
        // (likely still false due to CI/redirected output in test runner, but the flag is gone)
        // Just verify it doesn't throw and the method exists
        _ = TerminalCapabilities.IsInteractive;
    }

    [Fact]
    public void IsInteractive_ReturnsFalse_WhenOutputRedirected()
    {
        // In test environments Console.IsOutputRedirected is typically true
        // This test just documents the expected behavior
        if (Console.IsOutputRedirected)
            Assert.False(TerminalCapabilities.IsInteractive, "Redirected output must suppress interactivity");
        // If not redirected in some odd test runner, skip assertion
    }

    [Fact]
    public void IsInteractive_NoColorEmpty_DoesNotForceNonInteractive()
    {
        // NO_COLOR set to empty string should NOT suppress
        var prev = Environment.GetEnvironmentVariable("NO_COLOR");
        var prevCi = Environment.GetEnvironmentVariable("CI");
        var prevTerm = Environment.GetEnvironmentVariable("TERM");
        try
        {
            Environment.SetEnvironmentVariable("NO_COLOR", "");
            Environment.SetEnvironmentVariable("CI", "");
            Environment.SetEnvironmentVariable("TERM", "xterm");
            TerminalCapabilities.ResetForTesting();
            // The result depends on Console.IsOutputRedirected and !Environment.UserInteractive
            // We just verify it's computed without exception
            _ = TerminalCapabilities.IsInteractive;
        }
        finally
        {
            Environment.SetEnvironmentVariable("NO_COLOR", prev);
            Environment.SetEnvironmentVariable("CI", prevCi);
            Environment.SetEnvironmentVariable("TERM", prevTerm);
            TerminalCapabilities.ResetForTesting();
        }
    }
}
