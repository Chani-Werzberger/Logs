using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LogsPlatform.Web.Services;

public static partial class ExceptionFingerprinter
{
    public static string Compute(string exceptionType, string stackTrace, string? messageTemplate)
    {
        var signature = NormalizeStackSignature(stackTrace);
        var input = $"{exceptionType}|{signature}|{messageTemplate}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }

    private static string NormalizeStackSignature(string stackTrace)
    {
        var lines = stackTrace
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(3);

        var normalized = lines.Select(line => LineNumberPattern().Replace(line, string.Empty).Trim());
        return string.Join("\n", normalized);
    }

    [GeneratedRegex(@"\s+in\s+.*?:line \d+")]
    private static partial Regex LineNumberPattern();
}
