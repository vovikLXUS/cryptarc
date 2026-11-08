using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace CryptArc.App.Converters;

/// <summary>True → a 2 px border, false → none. Marks a block that failed verification.</summary>
public sealed class BoolToThicknessConverter : IValueConverter
{
    /// <summary>Shared instance for XAML.</summary>
    public static readonly BoolToThicknessConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? new Thickness(2) : new Thickness(0);

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
