namespace VeeamVhcAws.Core.State;

/// <summary>
/// The one rule for whether a state-file entry is suppressed, shared by the CLI
/// (<c>captures</c>) and the web Alerts and Captures pages. The top-level
/// <c>suppressions</c> map is what suppress writes. A monitor run copies it onto
/// <c>captured_errors[key].suppressed</c>, so either one marks the entry suppressed.
/// </summary>
public static class Suppression
{
    public static bool IsSuppressed(string key, Dictionary<string, object>? entry, Dictionary<string, object> suppressions) =>
        suppressions.ContainsKey(key) || entry?.GetValueOrDefault("suppressed") is true;

    /// <summary>
    /// Sets <c>captured_errors[key].suppressed</c>. Returns false when there is no captured
    /// entry for the key or the flag already has that value.
    /// </summary>
    public static bool MarkCaptured(Dictionary<string, object> captured, string key, bool suppressed)
    {
        if (captured.GetValueOrDefault(key) is not Dictionary<string, object> entry)
            return false;
        if (entry.GetValueOrDefault("suppressed") is bool current && current == suppressed)
            return false;
        entry["suppressed"] = suppressed;
        return true;
    }
}
