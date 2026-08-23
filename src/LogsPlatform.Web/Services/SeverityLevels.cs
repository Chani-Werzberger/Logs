namespace LogsPlatform.Web.Services;

public static class SeverityLevels
{
    public static readonly IReadOnlyDictionary<string, int> ByName = new Dictionary<string, int>
    {
        ["Trace"] = 1, ["Debug"] = 5, ["Info"] = 9, ["Warn"] = 13, ["Error"] = 17, ["Fatal"] = 21
    };

    public static readonly IReadOnlyDictionary<int, string> ByValue =
        ByName.ToDictionary(pair => pair.Value, pair => pair.Key);
}
