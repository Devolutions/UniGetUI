using System;
using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace UniGetUI.Avalonia.Converters;

/// <summary>
/// Reads AutomationProperties.Name from a cell's content, tolerating the null content that
/// virtualized DataGrid cells have while they are recycled.
/// </summary>
public sealed class AutomationNameOfContentConverter : IValueConverter
{
    public static readonly AutomationNameOfContentConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Control control
            ? AutomationProperties.GetName(control)
            : BindingOperations.DoNothing;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
