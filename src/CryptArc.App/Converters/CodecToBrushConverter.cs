using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CryptArc.Core;

namespace CryptArc.App.Converters;

/// <summary>
/// One colour per codec for the block map and its legend; null (a block still
/// pending) is a pale grey. Store is a neutral grey because a stored block is
/// "nothing happened".
/// </summary>
public sealed class CodecToBrushConverter : IValueConverter
{
    /// <summary>Shared instance for XAML.</summary>
    public static readonly CodecToBrushConverter Instance = new();

    private static readonly IBrush Pending = new ImmutableSolidColorBrush(Color.Parse("#E9ECF0"));

    private static readonly IBrush[] ByCodec =
    [
        new ImmutableSolidColorBrush(Color.Parse("#A3A9B3")), // Store
        new ImmutableSolidColorBrush(Color.Parse("#14B8A6")), // RLE
        new ImmutableSolidColorBrush(Color.Parse("#8B5CF6")), // Huffman
        new ImmutableSolidColorBrush(Color.Parse("#22C55E")), // LZSS
        new ImmutableSolidColorBrush(Color.Parse("#0064FA")), // Deflate
        new ImmutableSolidColorBrush(Color.Parse("#F59E0B")), // LZW
        new ImmutableSolidColorBrush(Color.Parse("#0EA5E9")), // BWT
        new ImmutableSolidColorBrush(Color.Parse("#EC4899")), // Arithmetic
    ];

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is CodecId id && (int)id < ByCodec.Length ? ByCodec[(int)id] : Pending;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
