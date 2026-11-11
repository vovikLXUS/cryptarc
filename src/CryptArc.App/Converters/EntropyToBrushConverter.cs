using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace CryptArc.App.Converters;

/// <summary>
/// Maps entropy in bits per byte (0…8) to a heatmap colour: cool blue for
/// compressible data, warm orange to red for data that is already near random.
/// </summary>
/// <remarks>
/// Five steps rather than a continuous gradient, so neighbouring blocks with a
/// meaningfully different entropy are told apart at a glance. The steps also
/// differ in lightness, not only in hue.
/// </remarks>
public sealed class EntropyToBrushConverter : IValueConverter
{
    /// <summary>Shared instance for XAML: <c>{x:Static conv:EntropyToBrushConverter.Instance}</c>.</summary>
    public static readonly EntropyToBrushConverter Instance = new();

    private static readonly IBrush[] Steps =
    [
        new ImmutableSolidColorBrush(Color.Parse(ThemePalette.ActiveBgHex)), // < 3   text, logs, sparse data
        new ImmutableSolidColorBrush(Color.Parse("#84A8D1")),                // < 5
        new ImmutableSolidColorBrush(Color.Parse(ThemePalette.SecondaryHex)),// < 6.5 typical binaries
        new ImmutableSolidColorBrush(Color.Parse("#E58E3C")),                // < 7.5
        new ImmutableSolidColorBrush(Color.Parse("#C93B2B")),                // ≥ 7.5 compressed or encrypted
    ];

    /// <summary>The legend's colours, low to high entropy.</summary>
    public static IBrush[] Legend => Steps;

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double entropy = value is double d ? d : 0;

        return entropy switch
        {
            < 3 => Steps[0],
            < 5 => Steps[1],
            < 6.5 => Steps[2],
            < 7.5 => Steps[3],
            _ => Steps[4],
        };
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
