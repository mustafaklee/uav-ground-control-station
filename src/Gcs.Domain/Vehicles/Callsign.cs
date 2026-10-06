using System.Text.RegularExpressions;
using Gcs.Domain.Common;

namespace Gcs.Domain.Vehicles;

/// <summary>
/// Human readable vehicle name used by operators on the radio and in the UI, e.g. <c>UAV-01</c>.
/// Stored upper case so "uav-01" and "UAV-01" cannot both exist.
/// </summary>
public sealed partial record Callsign
{
    public const int MinLength = 3;
    public const int MaxLength = 32;

    private Callsign(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static Result<Callsign> Create(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;

        if (normalized.Length is < MinLength or > MaxLength)
        {
            return VehicleErrors.CallsignLength;
        }

        if (!AllowedPattern().IsMatch(normalized))
        {
            return VehicleErrors.CallsignFormat;
        }

        return new Callsign(normalized);
    }

    public override string ToString() => Value;

    // Letters, digits and single hyphens between them: "UAV-01" is valid, "-UAV", "UAV--1" and "UAV 1" are not.
    [GeneratedRegex("^[A-Z0-9]+(-[A-Z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedPattern();
}
