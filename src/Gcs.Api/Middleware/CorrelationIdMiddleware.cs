using Serilog.Context;

namespace Gcs.Api.Middleware;

/// <summary>
/// Gives every request a correlation id (taken from the <c>X-Correlation-ID</c> header or generated),
/// echoes it in the response and attaches it to every log line written while the request is handled.
/// This is what lets us follow one operator action through API, database, broker and MAVLink logs.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-ID";
    public const string LogPropertyName = "CorrelationId";
    private const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var correlationId = ResolveCorrelationId(context.Request.Headers[HeaderName].ToString());
        context.TraceIdentifier = correlationId;

        // On the request's span too: search the trace view by the id a user reports from an error message.
        System.Diagnostics.Activity.Current?.SetTag(Gcs.Application.Diagnostics.GcsTracing.CorrelationId, correlationId);
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty(LogPropertyName, correlationId))
        {
            await next(context);
        }
    }

    /// <summary>
    /// Accepts a caller supplied id only if it is short and made of safe characters;
    /// anything else is replaced, so clients cannot inject arbitrary text into our logs.
    /// </summary>
    public static string ResolveCorrelationId(string? candidate) =>
        IsValid(candidate) ? candidate! : Guid.NewGuid().ToString("N");

    private static bool IsValid(string? candidate) =>
        !string.IsNullOrEmpty(candidate)
        && candidate.Length <= MaxLength
        && candidate.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
