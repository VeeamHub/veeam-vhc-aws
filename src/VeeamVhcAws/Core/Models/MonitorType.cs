using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeeamVhcAws.Core.Models;

[JsonConverter(typeof(MonitorTypeJsonConverter))]
public enum MonitorType
{
    RepoHealth,
    Retention,
    WorkerHealth,
    CrossCorrelation
}

public static class MonitorTypeExtensions
{
    private static readonly Dictionary<MonitorType, string> ToStringMap = new()
    {
        [MonitorType.RepoHealth] = "repo_health",
        [MonitorType.Retention] = "retention",
        [MonitorType.WorkerHealth] = "worker_health",
        [MonitorType.CrossCorrelation] = "cross_correlation",
    };

    private static readonly Dictionary<string, MonitorType> FromStringMap =
        ToStringMap.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    public static string ToLowerString(this MonitorType type) =>
        ToStringMap.GetValueOrDefault(type, "repo_health");

    public static MonitorType ParseMonitorType(string value) =>
        FromStringMap.GetValueOrDefault(value.ToLowerInvariant(), MonitorType.RepoHealth);
}

public class MonitorTypeJsonConverter : JsonConverter<MonitorType>
{
    public override MonitorType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString() ?? "repo_health";
        return MonitorTypeExtensions.ParseMonitorType(value);
    }

    public override void Write(Utf8JsonWriter writer, MonitorType value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToLowerString());
    }
}
