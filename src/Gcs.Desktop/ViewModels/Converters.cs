using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Gcs.Desktop.ViewModels;

/// <summary>Link state → status light colour (green connected, amber in progress, red faulted, grey off).</summary>
public sealed class LinkStateBrush : IValueConverter
{
    public static readonly LinkStateBrush Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        "Connected" => Brushes.LimeGreen,
        "Connecting" or "Reconnecting" => Brushes.Orange,
        "Faulted" => Brushes.Red,
        _ => Brushes.Gray,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Armed is shown in red: a vehicle with live motors is the most important thing on the screen.</summary>
public sealed class ArmedBrush : IValueConverter
{
    public static readonly ArmedBrush Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Brushes.OrangeRed : Brushes.LightGray;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BatteryBrush : IValueConverter
{
    public static readonly BatteryBrush Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Brushes.OrangeRed : Brushes.LimeGreen;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Optional number ↔ text box. Empty text means "not set" (null). Both "39.93" and "39,93" are accepted, because operators
/// on a Turkish keyboard type a comma; values are shown with a dot so coordinates look the same everywhere.
/// </summary>
public sealed class NullableDoubleConverter : IValueConverter
{
    public static readonly NullableDoubleConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double number ? number.ToString("0.#######", CultureInfo.InvariantCulture) : string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = (value as string)?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : Avalonia.Data.BindingOperations.DoNothing; // keep the last valid value while the operator is still typing
    }
}
