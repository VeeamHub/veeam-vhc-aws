using System.Text.Json;
using System.Text.Json.Serialization;

namespace VhcMonitor.Core.Models;

[JsonConverter(typeof(SeverityJsonConverter))]
public enum Severity
{
    Ok,
    Warning,
    Critical,
    Error
}

public static class SeverityExtensions
{
    private static readonly Dictionary<Severity, string> ToStringMap = new()
    {
        [Severity.Ok] = "ok",
        [Severity.Warning] = "warning",
        [Severity.Critical] = "critical",
        [Severity.Error] = "error",
    };

    private static readonly Dictionary<string, Severity> FromStringMap =
        ToStringMap.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<Severity, int> RankMap = new()
    {
        [Severity.Ok] = 0,
        [Severity.Warning] = 1,
        [Severity.Critical] = 2,
        [Severity.Error] = 3,
    };

    private static readonly Dictionary<Severity, int> ExitCodeMap = new()
    {
        [Severity.Ok] = 0,
        [Severity.Warning] = 1,
        [Severity.Critical] = 2,
        [Severity.Error] = 3,
    };

    public static string ToLowerString(this Severity severity) =>
        ToStringMap.GetValueOrDefault(severity, "ok");

    public static Severity ParseSeverity(string value) =>
        FromStringMap.GetValueOrDefault(value.ToLowerInvariant(), Severity.Warning);

    public static int Rank(this Severity severity) =>
        RankMap.GetValueOrDefault(severity, 0);

    public static int ToExitCode(this Severity severity) =>
        ExitCodeMap.GetValueOrDefault(severity, 0);
}

public class SeverityJsonConverter : JsonConverter<Severity>
{
    public override Severity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString() ?? "ok";
        return SeverityExtensions.ParseSeverity(value);
    }

    public override void Write(Utf8JsonWriter writer, Severity value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToLowerString());
    }
}
