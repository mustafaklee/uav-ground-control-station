using Gcs.Domain.Common;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Gcs.Api.Http;

/// <summary>
/// Maps expected failures to RFC 7807 problem responses. The stable error <c>code</c> is added to every response
/// so clients can react to a specific failure without parsing the human readable message.
/// </summary>
internal static class ErrorResults
{
    public const string CodeExtension = "code";

    public static IResult ToProblem(this Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var extensions = new Dictionary<string, object?> { [CodeExtension] = error.Code };

        return error.Type switch
        {
            ErrorType.Validation => TypedResults.ValidationProblem(
                error.Details, detail: error.Message, title: "Validation failed", extensions: extensions),
            ErrorType.NotFound => Problem(StatusCodes.Status404NotFound, "Not found", error, extensions),
            ErrorType.Conflict => Problem(StatusCodes.Status409Conflict, "Conflict", error, extensions),
            ErrorType.ConcurrencyConflict => Problem(StatusCodes.Status412PreconditionFailed, "Precondition failed", error, extensions),
            _ => throw new ArgumentOutOfRangeException(nameof(error), error.Type, "Unhandled error type."),
        };
    }

    public static IResult PreconditionRequired(string detail) => TypedResults.Problem(
        statusCode: StatusCodes.Status428PreconditionRequired,
        title: "Precondition required",
        detail: detail,
        extensions: new Dictionary<string, object?> { [CodeExtension] = "http.if_match.required" });

    public static IResult InvalidIfMatch() => TypedResults.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Invalid If-Match header",
        detail: "If-Match must contain a single entity tag returned by this API, for example \"3\".",
        extensions: new Dictionary<string, object?> { [CodeExtension] = "http.if_match.invalid" });

    private static ProblemHttpResult Problem(int statusCode, string title, Error error, Dictionary<string, object?> extensions) =>
        TypedResults.Problem(statusCode: statusCode, title: title, detail: error.Message, extensions: extensions);
}
