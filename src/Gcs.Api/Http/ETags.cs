using System.Globalization;

namespace Gcs.Api.Http;

/// <summary>
/// The entity version travels in HTTP headers: responses carry <c>ETag: "3"</c>, and an update must send
/// <c>If-Match: "3"</c> to say "apply this only if the vehicle is still at version 3".
/// </summary>
internal static class ETags
{
    public static string Format(int version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>Accepts a single strong ETag such as <c>"3"</c>. Weak (<c>W/"3"</c>), lists and <c>*</c> are rejected.</summary>
    public static bool TryParse(string? header, out int version)
    {
        version = 0;
        var value = header?.Trim();
        return value is { Length: > 2 }
            && value[0] == '"'
            && value[^1] == '"'
            && int.TryParse(value.AsSpan(1, value.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }
}
