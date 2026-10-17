namespace VeeamVhcAws.Web.Services;

public static class AlertLabels
{
    public static string KeyPreview(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        return key.Length <= 8 ? key : key[..8];
    }

    public static string Subject(AlertView alert)
    {
        if (!string.IsNullOrWhiteSpace(alert.Resource))
            return alert.Resource.Trim();
        var preview = KeyPreview(alert.Key);
        return string.IsNullOrEmpty(preview) ? "alert" : preview;
    }

    public static string Action(string verb, AlertView alert, IReadOnlyList<AlertView> siblings)
    {
        var subject = Subject(alert);
        var duplicates = 0;
        foreach (var other in siblings)
        {
            if (other.Suppressed == alert.Suppressed && string.Equals(Subject(other), subject, StringComparison.Ordinal))
                duplicates++;
        }

        if (duplicates > 1)
        {
            var preview = KeyPreview(alert.Key);
            return string.IsNullOrEmpty(preview) ? $"{verb} {subject}" : $"{verb} {subject} ({preview})";
        }

        return $"{verb} {subject}";
    }
}
