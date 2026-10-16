namespace VeeamVhcAws.Web.Services;

public static class OutputFieldText
{
    public static string Format(object? value)
    {
        switch (value)
        {
            case null:
                return "";
            case string text:
                return text;
            case System.Collections.IEnumerable items:
                var parts = new List<string>();
                foreach (var item in items)
                {
                    if (item is null)
                        continue;
                    parts.Add(item.ToString() ?? "");
                }
                return string.Join(", ", parts);
            default:
                return value.ToString() ?? "";
        }
    }
}
