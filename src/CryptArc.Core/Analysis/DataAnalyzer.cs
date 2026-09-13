namespace CryptArc.Core.Analysis;

/// <summary>
/// The "smart" half of the archiver: decides which codec a block should get,
/// based on a single cheap pass over a sample rather than trial compression.
/// </summary>
public static class DataAnalyzer
{
    /// <summary>Bytes inspected per block. Large enough to be representative, small enough to be free.</summary>
    public const int SampleSize = 64 * 1024;

    /// <summary>Above this entropy the data is effectively random — compressing it only wastes time.</summary>
    private const double IncompressibleEntropy = 7.8;

    /// <summary>Collects the order-0 statistics of <paramref name="data"/> in a single pass. O(n).</summary>
    public static DataProfile Analyze(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return new DataProfile(0, 0, 0, 0);

        ReadOnlySpan<byte> sample = data.Length > SampleSize ? data[..SampleSize] : data;

        Span<int> histogram = stackalloc int[256];
        histogram.Clear();

        int repeats = 0;
        int textBytes = 0;

        for (int i = 0; i < sample.Length; i++)
        {
            byte value = sample[i];
            histogram[value]++;

            if (i > 0 && value == sample[i - 1])
                repeats++;

            if (IsTextByte(value))
                textBytes++;
        }

        double entropy = 0;
        double length = sample.Length;

        foreach (int count in histogram)
        {
            if (count == 0)
                continue;

            double p = count / length;
            entropy -= p * Math.Log2(p);
        }

        return new DataProfile(
            SampledBytes: sample.Length,
            Entropy: entropy,
            RepeatRatio: sample.Length > 1 ? repeats / (length - 1) : 0,
            TextRatio: textBytes / length);
    }

    /// <summary>
    /// Maps a profile onto the codec that should handle the block.
    /// </summary>
    /// <remarks>
    /// The result is advisory: it may name a codec that is not implemented yet, so
    /// callers should go through <see cref="CodecRegistry.TryGet"/> and fall back to
    /// <see cref="CodecId.Store"/>. The thresholds are deliberately simple constants —
    /// replacing them with a trained decision tree is a planned follow-up.
    /// </remarks>
    public static CodecId Recommend(in DataProfile profile)
    {
        if (profile.SampledBytes == 0)
            return CodecId.Store;

        // Already compressed or encrypted: nothing left to squeeze out.
        if (profile.Entropy >= IncompressibleEntropy)
            return CodecId.Store;

        // Long identical stretches — bitmaps, sparse dumps, padded records.
        if (profile.RepeatRatio >= 0.30)
            return CodecId.Rle;

        // Natural language, source code, JSON, logs: block sorting wins on these.
        if (profile.TextRatio >= 0.90 && profile.Entropy <= 6.0)
            return CodecId.Bwt;

        // General purpose default.
        return CodecId.Deflate;
    }

    private static bool IsTextByte(byte value) =>
        value is >= 0x20 and <= 0x7E or (byte)'\t' or (byte)'\n' or (byte)'\r';
}
