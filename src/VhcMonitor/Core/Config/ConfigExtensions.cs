using System.Globalization;

namespace VhcMonitor.Core.Config;

public static class ConfigExtensions
{
    public static T Get<T>(this Dictionary<string, object> dict, string key, T defaultValue)
    {
        if (!dict.TryGetValue(key, out var value))
            return defaultValue;

        if (value is T typed)
            return typed;

        try
        {
            if (typeof(T) == typeof(int))
                return (T)(object)Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (typeof(T) == typeof(double))
                return (T)(object)Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (typeof(T) == typeof(float))
                return (T)(object)Convert.ToSingle(value, CultureInfo.InvariantCulture);
            if (typeof(T) == typeof(bool))
                return (T)(object)Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            if (typeof(T) == typeof(string))
                return (T)(object)Convert.ToString(value, CultureInfo.InvariantCulture)!;
            if (typeof(T) == typeof(long))
                return (T)(object)Convert.ToInt64(value, CultureInfo.InvariantCulture);

            return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
        }
        catch
        {
            return defaultValue;
        }
    }

    public static Dictionary<string, object> GetSection(this Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var value) && value is Dictionary<string, object> section)
            return section;

        // YamlDotNet may deserialize as Dictionary<object,object>
        if (dict.TryGetValue(key, out var rawValue) && rawValue is Dictionary<object, object> rawSection)
        {
            return rawSection.ToDictionary(
                kv => kv.Key?.ToString() ?? "",
                kv => kv.Value ?? (object)"");
        }

        return new Dictionary<string, object>();
    }

    public static List<Dictionary<string, object>> GetListOfSections(this Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value))
            return new List<Dictionary<string, object>>();

        if (value is List<object> list)
        {
            return list.Select(item =>
            {
                if (item is Dictionary<string, object> d) return d;
                if (item is Dictionary<object, object> raw)
                    return raw.ToDictionary(
                        kv => kv.Key?.ToString() ?? "",
                        kv => kv.Value ?? (object)"");
                return new Dictionary<string, object>();
            }).ToList();
        }

        if (value is List<Dictionary<string, object>> typedList)
            return typedList;

        return new List<Dictionary<string, object>>();
    }

    /// <summary>
    /// Get a value from a dictionary trying both camelCase and PascalCase keys (VBR API inconsistency).
    /// </summary>
    public static object? GetApi(this Dictionary<string, object> dict, string camelKey, string pascalKey, object? defaultValue = null)
    {
        if (dict.TryGetValue(camelKey, out var v1)) return v1;
        if (dict.TryGetValue(pascalKey, out var v2)) return v2;
        return defaultValue;
    }

    public static string GetApiString(this Dictionary<string, object> dict, string camelKey, string pascalKey, string defaultValue = "")
    {
        var val = dict.GetApi(camelKey, pascalKey, defaultValue);
        if (val is IList<object> list)
            return string.Join(", ", list);
        return val?.ToString() ?? defaultValue;
    }

    public static double GetApiDouble(this Dictionary<string, object> dict, string camelKey, string pascalKey, double defaultValue = 0)
    {
        var val = dict.GetApi(camelKey, pascalKey, defaultValue);
        if (val is double d) return d;
        if (double.TryParse(val?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return defaultValue;
    }

    public static int GetApiInt(this Dictionary<string, object> dict, string camelKey, string pascalKey, int defaultValue = 0)
    {
        var val = dict.GetApi(camelKey, pascalKey, defaultValue);
        if (val is int i) return i;
        if (int.TryParse(val?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return defaultValue;
    }

    public static bool GetApiBool(this Dictionary<string, object> dict, string camelKey, string pascalKey, bool defaultValue = false)
    {
        var val = dict.GetApi(camelKey, pascalKey, defaultValue);
        if (val is bool b) return b;
        if (bool.TryParse(val?.ToString(), out var parsed)) return parsed;
        return defaultValue;
    }
}
