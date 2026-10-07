namespace Gcs.Desktop.Services;

/// <summary>Where the backend runs. Override with <c>--api http://host:port/</c> or the <c>GCS_API_URL</c> environment variable.</summary>
public sealed record GcsClientOptions(Uri ApiBaseUrl)
{
    public const string EnvironmentVariable = "GCS_API_URL";
    public const string CommandLineSwitch = "--api";
    public static readonly Uri DefaultApiBaseUrl = new("http://localhost:8080/");

    public static GcsClientOptions FromEnvironment(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var fromArgs = args.SkipWhile(a => a != CommandLineSwitch).Skip(1).FirstOrDefault();
        var raw = fromArgs ?? Environment.GetEnvironmentVariable(EnvironmentVariable);
        var url = Uri.TryCreate(raw, UriKind.Absolute, out var parsed) ? parsed : DefaultApiBaseUrl;

        // HttpClient.BaseAddress needs a trailing slash for relative paths to resolve under it.
        return new GcsClientOptions(url.AbsoluteUri.EndsWith('/') ? url : new Uri(url.AbsoluteUri + "/"));
    }
}
