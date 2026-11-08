namespace CryptArc.Core.Coding;

/// <summary>
/// The byte-level Huffman code that <see cref="Codecs.HuffmanCodec"/> would build for a
/// buffer, exposed for display and teaching: frequencies, code lengths and codes.
/// </summary>
/// <remarks>
/// <para>
/// Same construction as the codec — <see cref="CanonicalHuffman.BuildLengths"/> with a
/// 15-bit limit, then canonical code assignment — so what a front end shows is what the
/// codec writes.
/// </para>
/// <para>
/// Codes are returned most-significant-bit first, the way they are usually written down
/// (<c>0</c>, <c>10</c>, <c>110</c>…). The codec itself stores them bit-reversed because
/// its bit stream is least-significant-bit first; see <see cref="CanonicalHuffman"/>.
/// </para>
/// </remarks>
public static class HuffmanCodeBook
{
    /// <summary>The longest code, the same limit <see cref="Codecs.HuffmanCodec"/> uses.</summary>
    public const int MaxCodeLength = 15;

    /// <summary>Number of symbols: one per byte value.</summary>
    public const int AlphabetSize = 256;

    /// <summary>Builds the code for <paramref name="data"/>.</summary>
    /// <param name="data">The bytes to code.</param>
    /// <param name="frequencies">Receives how often each byte value occurs; 256 entries.</param>
    /// <param name="lengths">Receives each byte value's code length, 0 when it does not occur; 256 entries.</param>
    /// <param name="codes">Receives each byte value's code, most significant bit first, 0 when unused; 256 entries.</param>
    /// <returns>The coded size of <paramref name="data"/> in bits, without any table header.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An output span is shorter than 256.</exception>
    public static long Build(ReadOnlySpan<byte> data, Span<int> frequencies, Span<byte> lengths, Span<uint> codes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frequencies.Length, AlphabetSize, nameof(frequencies));
        ArgumentOutOfRangeException.ThrowIfLessThan(lengths.Length, AlphabetSize, nameof(lengths));
        ArgumentOutOfRangeException.ThrowIfLessThan(codes.Length, AlphabetSize, nameof(codes));

        frequencies = frequencies[..AlphabetSize];
        lengths = lengths[..AlphabetSize];
        codes = codes[..AlphabetSize];

        frequencies.Clear();
        foreach (byte value in data)
            frequencies[value]++;

        lengths.Clear();
        codes.Clear();
        if (data.IsEmpty)
            return 0;

        CanonicalHuffman.BuildLengths(frequencies, lengths, MaxCodeLength);
        CanonicalHuffman.AssignCodes(lengths, codes, MaxCodeLength);

        long bits = 0;
        for (int symbol = 0; symbol < AlphabetSize; symbol++)
        {
            if (lengths[symbol] == 0)
                continue;

            codes[symbol] = CanonicalHuffman.ReverseBits(codes[symbol], lengths[symbol]);
            bits += (long)frequencies[symbol] * lengths[symbol];
        }

        return bits;
    }
}
