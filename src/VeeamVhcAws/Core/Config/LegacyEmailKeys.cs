using System.Text.RegularExpressions;

namespace VeeamVhcAws.Core.Config;

public static class LegacyEmailKeys
{
    private static readonly Regex FromLine = new(@"^(\s+)from:\s", RegexOptions.Compiled);
    private static readonly Regex ToLine = new(@"^(\s+)to:\s", RegexOptions.Compiled);

    public static void Migrate(Dictionary<string, object> config)
    {
        if (!config.TryGetValue("output", out var output) || output is not List<object> list)
            return;

        foreach (var item in list)
        {
            if (item is Dictionary<string, object> typed && IsEmail(typed))
            {
                Promote(typed, "from", "from_addr");
                Promote(typed, "to", "to_addrs");
            }
            else if (item is Dictionary<object, object> raw && IsEmail(raw))
            {
                Promote(raw, "from", "from_addr");
                Promote(raw, "to", "to_addrs");
            }
        }
    }

    public static int MigrateLines(string[] lines)
    {
        var edits = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var updated = MigrateLine(lines[i]);
            if (updated == lines[i])
                continue;
            lines[i] = updated;
            edits++;
        }
        return edits;
    }

    public static string MigrateLine(string line)
    {
        var updated = FromLine.Replace(line, "$1from_addr: ");
        return ToLine.Replace(updated, "$1to_addrs: ");
    }

    private static bool IsEmail(Dictionary<string, object> entry) =>
        string.Equals(entry.Get("type", ""), "email", StringComparison.OrdinalIgnoreCase);

    private static bool IsEmail(Dictionary<object, object> entry)
    {
        foreach (var (key, value) in entry)
        {
            if (string.Equals(key?.ToString(), "type", StringComparison.OrdinalIgnoreCase)
                && string.Equals(value?.ToString(), "email", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void Promote(Dictionary<string, object> entry, string legacy, string current)
    {
        if (!entry.TryGetValue(legacy, out var legacyValue))
            return;
        if (!entry.TryGetValue(current, out var currentValue) || IsBlank(currentValue))
            entry[current] = legacyValue;
        entry.Remove(legacy);
    }

    private static void Promote(Dictionary<object, object> entry, string legacy, string current)
    {
        object? legacyKey = null;
        object? currentKey = null;
        object? legacyValue = null;
        object? currentValue = null;
        foreach (var (key, value) in entry)
        {
            var name = key?.ToString();
            if (name == legacy)
            {
                legacyKey = key;
                legacyValue = value;
            }
            else if (name == current)
            {
                currentKey = key;
                currentValue = value;
            }
        }
        if (legacyKey is null)
            return;
        if (currentKey is null || IsBlank(currentValue))
            entry[current] = legacyValue!;
        entry.Remove(legacyKey);
    }

    private static bool IsBlank(object? value) =>
        value is null || (value is string text && string.IsNullOrWhiteSpace(text));
}
