using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Clicky.App;

/// <summary>
/// Converts non-null values to Visible and null/empty strings to Collapsed.
/// Used for showing inline error text only when an error message is set.
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return string.IsNullOrEmpty(value as string)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
