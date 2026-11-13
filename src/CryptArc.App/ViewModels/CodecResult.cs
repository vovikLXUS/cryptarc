using CryptArc.App.Localization;

namespace CryptArc.App.ViewModels;

/// <summary>One row of the "how did each algorithm do" table shown in the UI.</summary>
/// <param name="Codec">Display name of the algorithm.</param>
/// <param name="CompressedSize">Size in bytes after compression.</param>
/// <param name="Ratio">Compressed size as a fraction of the original.</param>
/// <param name="EncodeThroughput">Compression speed in MB/s.</param>
/// <param name="DecodeThroughput">Decompression speed in MB/s.</param>
/// <param name="RoundTripOk">Whether the data survived a compress/decompress cycle intact.</param>
/// <remarks>
/// The display texts are fixed when the row is created, in the language of that moment,
/// and are part of the record's equality. A data grid keeps the cells of a row it
/// considers unchanged, so after a language switch the rows are created again
/// (<see cref="Retranslated"/>) rather than asked for their texts a second time.
/// For the same reason a <c>with</c> copy keeps the texts of the original: change a
/// value by creating a new row, not with <c>with</c>.
/// </remarks>
public sealed record CodecResult(
    string Codec,
    long CompressedSize,
    double Ratio,
    double EncodeThroughput,
    double DecodeThroughput,
    bool RoundTripOk)
{
    /// <summary>Compressed size for display.</summary>
    public string SizeText { get; } = Localizer.Instance.Format("Unit.Bytes", CompressedSize);

    /// <summary>Ratio for display.</summary>
    public string RatioText { get; } = Localizer.Instance.Number(Ratio, "P1");

    /// <summary>Encode and decode speed for display.</summary>
    public string SpeedText { get; } = Localizer.Instance.Format("Compare.Speed.Value", EncodeThroughput, DecodeThroughput);

    /// <summary>Round-trip outcome for display.</summary>
    public string StatusText { get; } = Localizer.Instance[RoundTripOk ? "Compare.Ok" : "Compare.Mismatch"];

    /// <summary>The same result with its texts in the current language.</summary>
    /// <returns>A new row.</returns>
    public CodecResult Retranslated() =>
        new(Codec, CompressedSize, Ratio, EncodeThroughput, DecodeThroughput, RoundTripOk);
}
