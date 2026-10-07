using System.Net;
using System.Text.Json;

namespace Gcs.Desktop.Services;

/// <summary>
/// An expected API failure (400 validation, 404, 409, 412) with the server's stable error code and messages, so the
/// UI can show "Altitude must be between 2 and 500 m" instead of "Response status code does not indicate success: 400".
/// </summary>
public sealed class ApiProblemException : Exception
{
    public ApiProblemException()
    {
    }

    public ApiProblemException(string message)
        : base(message)
    {
    }

    public ApiProblemException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ApiProblemException(HttpStatusCode status, string? code, string message, IReadOnlyDictionary<string, string[]> fieldErrors)
        : base(message)
    {
        Status = status;
        Code = code;
        FieldErrors = fieldErrors;
    }

    public HttpStatusCode Status { get; }

    public string? Code { get; }

    public IReadOnlyDictionary<string, string[]> FieldErrors { get; } = new Dictionary<string, string[]>();

    /// <summary>The detail plus every field error on its own line.</summary>
    public string Describe() =>
        FieldErrors.Count == 0
            ? Message
            : $"{Message}{Environment.NewLine}{string.Join(Environment.NewLine, FieldErrors.Select(e => $"{e.Key}: {string.Join(" ", e.Value)}"))}";

    public static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? code = null;
        var message = $"{(int)response.StatusCode} {response.ReasonPhrase}";
        var errors = new Dictionary<string, string[]>();
        try
        {
            using var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = problem.RootElement;
            code = root.TryGetProperty("code", out var c) ? c.GetString() : null;
            message = root.TryGetProperty("detail", out var d) && d.GetString() is { } detail ? detail : message;
            if (root.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in e.EnumerateObject())
                {
                    errors[field.Name] = [.. field.Value.EnumerateArray().Select(v => v.GetString() ?? string.Empty)];
                }
            }
        }
        catch (JsonException)
        {
            // Not a problem document (e.g. a proxy error page): keep the status line.
        }

        throw new ApiProblemException(response.StatusCode, code, message, errors);
    }
}
