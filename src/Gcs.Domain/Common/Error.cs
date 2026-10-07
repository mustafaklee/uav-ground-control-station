namespace Gcs.Domain.Common;

/// <summary>
/// An expected failure (invalid input, missing entity, rule violation). Expected failures are returned as values
/// instead of thrown, so callers must handle them and exceptions stay reserved for real faults (database down, bugs).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification = "C# only codebase; 'Error' is a VB keyword and does not affect C# callers.")]
public sealed record Error(string Code, string Message, ErrorType Type)
{
    /// <summary>Field-level validation messages, keyed by field name. Empty for non-validation errors.</summary>
    public IReadOnlyDictionary<string, string[]> Details { get; init; } = new Dictionary<string, string[]>();

    public static Error Validation(string code, string message) => new(code, message, ErrorType.Validation);

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    public static Error ConcurrencyConflict(string code, string message) => new(code, message, ErrorType.ConcurrencyConflict);

    public static Error Timeout(string code, string message) => new(code, message, ErrorType.Timeout);
}

public enum ErrorType
{
    /// <summary>The input breaks a rule (bad format, out of range).</summary>
    Validation,

    /// <summary>The referenced entity does not exist.</summary>
    NotFound,

    /// <summary>The operation clashes with current state (duplicate callsign, vehicle retired).</summary>
    Conflict,

    /// <summary>The caller edited an outdated version; someone else changed the entity in between.</summary>
    ConcurrencyConflict,

    /// <summary>Something outside the GCS (a vehicle) did not answer in time. The outcome is unknown, not "failed".</summary>
    Timeout,
}
