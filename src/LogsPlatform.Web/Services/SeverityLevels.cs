namespace LogsPlatform.Web.Services;

public static class SeverityLevels
{
    public static readonly IReadOnlyDictionary<string, int> ByName = new Dictionary<string, int>
    {
        ["Trace"] = 1, ["Debug"] = 5, ["Info"] = 9, ["Warn"] = 13, ["Error"] = 17, ["Fatal"] = 21
    };

    public static readonly IReadOnlyDictionary<int, string> ByValue =
        ByName.ToDictionary(pair => pair.Value, pair => pair.Key);

    // OTel SeverityNumber is 1-24 in 4-wide bands (TRACE=1-4, DEBUG=5-8, ..., FATAL=21-24);
    // this project's own severity values already align to each band's first number.
    public static string? FromOtelSeverityNumber(int severityNumber)
    {
        if (severityNumber < 1 || severityNumber > 24)
        {
            return null;
        }
        var band = ((severityNumber - 1) / 4) * 4 + 1;
        return ByValue[band];
    }
}
