using Xunit;
using VhcMonitor.Core.Models;
using VhcMonitor.Core.Patterns;

namespace VhcMonitor.Tests.Core;

public class PatternEngineTests
{
    private static PatternEngine BuildEngine()
    {
        return new PatternEngine(new List<ErrorPattern>
        {
            new(
                pattern: @"(?i)access\s*key.*(?:invalid|expired|does not exist)",
                severity: Severity.Critical,
                message: "AWS credential failure",
                category: "credential",
                extractFields: new List<string> { "key_id" }
            ),
            new(
                pattern: @"(?i)(?:cannot allocate|insufficient\s*free\s*addresses).*(?:subnet[- ]?(?P<subnet_id>subnet-[a-z0-9]+))",
                severity: Severity.Critical,
                message: "Subnet IP exhaustion",
                category: "network",
                extractFields: new List<string> { "subnet_id" },
                logHint: "/var/log/veeam/worker.log",
                remediation: "Add a secondary subnet or increase CIDR range"
            ),
            new(
                pattern: @"(?i)s3.*(?:timeout|connection refused|503)",
                severity: Severity.Critical,
                message: "S3 connectivity failure",
                category: "s3"
            ),
            new(
                pattern: @"(?i)authentication failed|unauthorized|401",
                severity: Severity.Critical,
                message: "Authentication failure",
                category: "auth"
            ),
        });
    }

    [Fact]
    public void ClassifyCredentialPattern()
    {
        var engine = BuildEngine();
        var result = engine.Classify("The AWS Access Key Id you provided does not exist in our records");
        Assert.NotNull(result);
        Assert.Equal("credential", result.Category);
        Assert.Equal(Severity.Critical, result.Severity);
    }

    [Fact]
    public void ClassifySubnetPattern()
    {
        var engine = BuildEngine();
        var result = engine.Classify("Cannot allocate IP address in subnet subnet-0abc123def456. InsufficientFreeAddressesInSubnet");
        Assert.NotNull(result);
        Assert.Equal("network", result.Category);
    }

    [Fact]
    public void ClassifyNoMatch()
    {
        var engine = BuildEngine();
        var result = engine.Classify("Everything is fine, no errors here");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractSubnetId()
    {
        var engine = BuildEngine();
        var pattern = engine.Classify("Cannot allocate IP address in subnet subnet-0abc123def456. InsufficientFreeAddressesInSubnet");
        Assert.NotNull(pattern);
        var extracted = engine.Extract("Cannot allocate IP address in subnet subnet-0abc123def456. InsufficientFreeAddressesInSubnet", pattern);
        Assert.Equal("subnet-0abc123def456", extracted.GetValueOrDefault("subnet_id"));
    }

    [Fact]
    public void FromConfig()
    {
        var configList = new List<Dictionary<string, object>>
        {
            new()
            {
                ["pattern"] = @"disk full",
                ["severity"] = "critical",
                ["message"] = "Disk full",
                ["category"] = "storage",
            },
            new()
            {
                ["pattern"] = @"warning.*threshold",
                ["severity"] = "warning",
                ["message"] = "Threshold warning",
                ["category"] = "capacity",
            },
        };
        var engine = PatternEngine.FromConfig(configList);
        var result = engine.Classify("disk full on volume D:");
        Assert.NotNull(result);
        Assert.Equal("storage", result.Category);
        Assert.Equal(Severity.Critical, result.Severity);

        var result2 = engine.Classify("warning: threshold exceeded");
        Assert.NotNull(result2);
        Assert.Equal("capacity", result2.Category);
        Assert.Equal(Severity.Warning, result2.Severity);
    }
}
