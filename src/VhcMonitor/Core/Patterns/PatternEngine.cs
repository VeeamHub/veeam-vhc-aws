using System.Text.RegularExpressions;
using VhcMonitor.Core.Config;
using VhcMonitor.Core.Models;

namespace VhcMonitor.Core.Patterns;

public class PatternEngine
{
    private readonly List<(Regex Compiled, ErrorPattern Pattern)> _patterns;

    public IReadOnlyList<ErrorPattern> Patterns =>
        _patterns.Select(p => p.Pattern).ToList().AsReadOnly();

    // Translate Python-style named groups (?P<name>...) to .NET-style (?<name>...)
    private static string NormalizePattern(string pattern) =>
        Regex.Replace(pattern, @"\(\?P<", "(?<");

    public PatternEngine(IEnumerable<ErrorPattern> patterns)
    {
        _patterns = patterns.Select(p =>
        {
            var normalized = NormalizePattern(p.Pattern);
            return (new Regex(normalized, RegexOptions.IgnoreCase | RegexOptions.Compiled), p);
        }).ToList();
    }

    public ErrorPattern? Classify(string errorText)
    {
        foreach (var (compiled, pattern) in _patterns)
        {
            if (compiled.IsMatch(errorText))
                return pattern;
        }
        return null;
    }

    public Dictionary<string, string> Extract(string errorText, ErrorPattern pattern)
    {
        var compiled = new Regex(NormalizePattern(pattern.Pattern), RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var match = compiled.Match(errorText);
        if (!match.Success)
            return new Dictionary<string, string>();

        var result = new Dictionary<string, string>();
        foreach (var fieldName in pattern.ExtractFields)
        {
            var group = match.Groups[fieldName];
            if (group.Success)
                result[fieldName] = group.Value;
        }
        return result;
    }

    public static PatternEngine FromConfig(List<Dictionary<string, object>> configList)
    {
        var patterns = new List<ErrorPattern>();
        foreach (var entry in configList)
        {
            var sevStr = entry.Get("severity", "warning").ToLowerInvariant();
            var severity = SeverityExtensions.ParseSeverity(sevStr);

            var extractFields = new List<string>();
            if (entry.TryGetValue("extract_fields", out var ef))
            {
                if (ef is List<object> list)
                    extractFields = list.Select(x => x.ToString() ?? "").ToList();
                else if (ef is List<string> sList)
                    extractFields = sList;
            }

            patterns.Add(new ErrorPattern(
                pattern: entry.Get("pattern", ""),
                severity: severity,
                message: entry.Get("message", ""),
                category: entry.Get("category", "unknown"),
                extractFields: extractFields,
                source: entry.Get("source", ""),
                logHint: entry.Get("log_hint", ""),
                remediation: entry.Get("remediation", "")
            ));
        }
        return new PatternEngine(patterns);
    }
}
