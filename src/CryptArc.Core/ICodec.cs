namespace CryptArc.Core;

/// <summary>
/// A block codec. Every algorithm in the project implements this contract so the
/// container, the analyzer and the benchmarks can treat them interchangeably.
/// </summary>
/// <remarks>
/// Codecs are stateless and must be safe to call from several threads at once:
/// blocks are compressed in parallel.
/// </remarks>
public interface ICodec
{
    CodecId Id { get; }

    /// <summary>Human readable name used in CLI output and benchmark tables.</summary>
    string Name { get; }

    /// <summary>
    /// Upper bound on the output size for <paramref name="sourceLength"/> input bytes,
    /// including the worst case where the data is incompressible.
    /// </summary>
    int MaxCompressedSize(int sourceLength);

    /// <summary>
    /// Compresses <paramref name="source"/> into <paramref name="destination"/>.
    /// Returns the number of bytes written, or -1 when the destination was too small.
    /// A -1 is not an error: the caller falls back to <see cref="CodecId.Store"/>.
    /// </summary>
    int Encode(ReadOnlySpan<byte> source, Span<byte> destination);

    /// <summary>
    /// Decompresses <paramref name="source"/> into <paramref name="destination"/>.
    /// Returns the number of bytes written.
    /// </summary>
    /// <exception cref="InvalidDataException">The input is malformed or truncated.</exception>
    int Decode(ReadOnlySpan<byte> source, Span<byte> destination);
}
