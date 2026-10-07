using System.Text.RegularExpressions;

namespace Gcs.Desktop.Services;

/// <summary>
/// Where the backend runs and who is operating. Override with <c>--api http://host:port/</c> / <c>GCS_API_URL</c> and
/// <c>--operator name</c> / <c>GCS_OPERATOR</c>. The operator defaults to the Windows/Linux user name; sign-in replaces
/// this in Phase 8.
/// </summary>
public sealed partial record GcsClientOptions(Uri ApiBaseUrl, string OperatorName)
{
    public const string EnvironmentVariable = "GCS_API_URL";
    public const string CommandLineSwitch = "--api";
    public const string OperatorEnvironmentVariable = "GCS_OPERATOR";
    public const string OperatorSwitch = "--operator";
    public static readonly Uri DefaultApiBaseUrl = new("http://localhost:8080/");

    public static GcsClientOptions FromEnvironment(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var raw = Argument(args, CommandLineSwitch) ?? Environment.GetEnvironmentVariable(EnvironmentVariable);
        var url = Uri.TryCreate(raw, UriKind.Absolute, out var parsed) ? parsed : DefaultApiBaseUrl;

        var operatorName = Argument(args, OperatorSwitch) ?? Environment.GetEnvironmentVariable(OperatorEnvironmentVariable) ?? Environment.UserName;

        // HttpClient.BaseAddress needs a trailing slash for relative paths to resolve under it.
        return new GcsClientOptions(url.AbsoluteUri.EndsWith('/') ? url : new Uri(url.AbsoluteUri + "/"), Sanitize(operatorName));
    }

    /// <summary>The API accepts letters, digits and . _ @ - (up to 64); anything else in a user name becomes '-'.</summary>
    public static string Sanitize(string? operatorName)
    {
        var cleaned = NotAllowed().Replace(operatorName?.Trim() ?? string.Empty, "-");
        return cleaned.Length == 0 ? "operator" : cleaned[..Math.Min(cleaned.Length, 64)];
    }

    private static string? Argument(IReadOnlyList<string> args, string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();

    [GeneratedRegex(@"[^A-Za-z0-9._@\-]", RegexOptions.CultureInvariant)]
    private static partial Regex NotAllowed();
}
