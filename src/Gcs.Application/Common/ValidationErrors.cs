using FluentValidation.Results;
using Gcs.Domain.Common;

namespace Gcs.Application.Common;

internal static class ValidationErrors
{
    public const string Code = "validation.failed";

    /// <summary>Turns validator output into one <see cref="Error"/> with per-field messages, keyed in camelCase like the JSON.</summary>
    public static Error From(ValidationResult validation) =>
        new(Code, "One or more fields are invalid.", ErrorType.Validation)
        {
            Details = validation.Errors
                .GroupBy(failure => ToCamelCasePath(failure.PropertyName))
                .ToDictionary(group => group.Key, group => group.Select(failure => failure.ErrorMessage).Distinct().ToArray()),
        };

    private static string ToCamelCasePath(string propertyPath) =>
        string.Join('.', propertyPath.Split('.').Select(segment =>
            segment.Length == 0 ? segment : char.ToLowerInvariant(segment[0]) + segment[1..]));
}
