namespace CryptArc.Core.Analysis;

/// <summary>
/// Cheap statistical summary of a block, produced by <see cref="DataAnalyzer"/>
/// and used to pick a codec without trying them all.
/// </summary>
/// <param name="SampledBytes">How many bytes the statistics were taken over.</param>
/// <param name="Entropy">Shannon entropy in bits per byte, 0…8.</param>
/// <param name="RepeatRatio">Fraction of positions whose byte equals the previous one.</param>
/// <param name="TextRatio">Fraction of bytes that are printable ASCII or common whitespace.</param>
public readonly record struct DataProfile(
    int SampledBytes,
    double Entropy,
    double RepeatRatio,
    double TextRatio)
{
    /// <summary>
    /// Theoretical floor on the compression ratio implied by the order-0 entropy.
    /// A value near 1 means no entropy coder will help.
    /// </summary>
    public double EntropyBound => Entropy / 8.0;
}
