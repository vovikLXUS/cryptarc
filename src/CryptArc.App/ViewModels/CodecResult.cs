namespace CryptArc.App.ViewModels;

/// <summary>One row of the "how did each algorithm do" table shown in the UI.</summary>
/// <param name="Codec">Display name of the algorithm.</param>
/// <param name="CompressedSize">Size in bytes after compression.</param>
/// <param name="Ratio">Compressed size as a fraction of the original.</param>
/// <param name="EncodeThroughput">Compression speed in MB/s.</param>
/// <param name="DecodeThroughput">Decompression speed in MB/s.</param>
/// <param name="RoundTripOk">Whether the data survived a compress/decompress cycle intact.</param>
public sealed record CodecResult(
    string Codec,
    long CompressedSize,
    double Ratio,
    double EncodeThroughput,
    double DecodeThroughput,
    bool RoundTripOk)
{
    public string SizeText => $"{CompressedSize:N0} B";

    public string RatioText => $"{Ratio:P1}";

    public string SpeedText => $"{EncodeThroughput:F1} / {DecodeThroughput:F1} MB/s";

    public string StatusText => RoundTripOk ? "OK" : "MISMATCH";
}
