using System.Diagnostics.CodeAnalysis;

namespace Gcs.Domain.Common;

/// <summary>
/// Outcome of an operation that can fail in an expected way. Either <see cref="IsSuccess"/> is true,
/// or <see cref="Error"/> describes why it failed.
/// </summary>
public class Result
{
    protected Result(Error? error)
    {
        Error = error;
    }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    public Error? Error { get; }

    public static Result Success() => new(null);

    public static Result Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(error);
    }

    public static implicit operator Result(Error error) => Failure(error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T value)
        : base(null)
    {
        _value = value;
    }

    private Result(Error error)
        : base(error)
    {
    }

    /// <summary>The value of a successful result. Reading it from a failed result is a programming error.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot read the value of a failed result ({Error.Code}).");

    // Results are created through these conversions: "return vehicle;" or "return VehicleErrors.NotFound;".
    public static implicit operator Result<T>(T value) => new(value);

    public static implicit operator Result<T>(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(error);
    }
}
