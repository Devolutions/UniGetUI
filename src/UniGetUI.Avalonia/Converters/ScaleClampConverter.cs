using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace UniGetUI.Avalonia.Converters;

/// <summary>
/// Multiplies a bound double (typically a container dimension) by a fraction and clamps the
/// result to an upper bound, so an element can scale down with its container yet never grow
/// past a sensible maximum. The converter parameter is "fraction|max" (e.g. "0.5|240").
/// </summary>
public sealed class ScaleClampConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double available || double.IsNaN(available) || available <= 0)
            return 0.0;

        double fraction = 1.0;
        double max = double.PositiveInfinity;

        if (parameter is string param)
        {
            string[] parts = param.Split('|');
            if (parts.Length > 0 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double f))
                fraction = f;
            if (parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double m))
                max = m;
        }

        return Math.Min(available * fraction, max);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
