using System.Buffers.Binary;
using CryptArc.Core.Coding;
using CryptArc.Core.IO;

namespace CryptArc.Core.Codecs;

/// <summary>
/// Canonical Huffman coding over bytes, with code lengths limited to 15 bits.
/// </summary>
/// <remarks>
/// <para>Stream format — an empty input encodes to zero bytes, anything else is:</para>
/// <list type="bullet">
///   <item><description>
///     Bytes 0…3 — number of encoded symbols (the original length), little-endian.
///   </description></item>
///   <item><description>
///     Bytes 4…131 — code length of each of the 256 symbols, 4 bits each. Symbol
///     <c>2i</c> is the low nibble of byte <c>4 + i</c>, symbol <c>2i + 1</c> the high
///     nibble. Zero means the symbol does not occur.
///   </description></item>
///   <item><description>
///     Bytes 132… — the codes, written through <see cref="BitWriter"/> and zero padded
///     up to a whole byte.
///   </description></item>
/// </list>
/// <para>
/// Only the lengths are stored: canonical codes are fully determined by them. Symbols
/// are ordered by (length, value) and numbered consecutively, so the decoder rebuilds
/// the same codes. A canonical code is defined first-bit-is-most-significant while the
/// bit streams are least-significant-first, so every code is bit-reversed before use.
/// </para>
/// <para>
/// The lengths must form a complete prefix code (Kraft sum of exactly 1). The one
/// exception is a single distinct symbol, which gets the 1-bit code <c>0</c>: a tree
/// with one leaf has depth 0, which could not encode anything.
/// </para>
/// <para>
/// Worst case: an optimal code never loses to a flat 8-bit code, so the bit stream
/// takes at most <c>n</c> bytes and only the 132-byte header is overhead. This holds
/// for the limited code too, because a flat 8-bit code fits within the 15-bit limit.
/// </para>
/// <para>
/// Complexity, with k ≤ 256 distinct symbols and L = 15. Encoding: O(n + k log k) time,
/// plus O(k·L) on the rare blocks that need limiting. Decoding: O(n) time with
/// <see cref="Decode"/>, O(n·L) with <see cref="DecodeTreeWalk"/>. Both directions use
/// O(1) additional space, all of it on the stack.
/// </para>
/// </remarks>
public sealed class HuffmanCodec : ICodec
{
    private const int SymbolCount = 256;
    private const int MaxCodeLength = 15;

    private const int LengthTableSize = SymbolCount / 2;
    private const int HeaderSize = sizeof(uint) + LengthTableSize;

    // Decoder table layout: 1024 root entries indexed by the next 10 bits, followed by
    // subtables for the longer codes. A subtable covers the remaining 5 bits at most and,
    // the code being complete, holds at least two leaves, so there are at most 128 of them.
    private const int RootBits = 10;
    private const int RootSize = 1 << RootBits;
    private const int MaxSubtableBits = MaxCodeLength - RootBits;
    private const int DecodeTableSize = RootSize + (SymbolCount / 2 << MaxSubtableBits);

    /// <summary>Always <see cref="CodecId.Huffman"/>.</summary>
    public CodecId Id => CodecId.Huffman;

    /// <summary>Always "Huffman".</summary>
    public string Name => "Huffman";

    /// <summary>
    /// The source length plus the 132-byte header, or 0 for an empty source. The bit
    /// stream never takes more than one byte per symbol; see the remarks on this class.
    /// </summary>
    public int MaxCompressedSize(int sourceLength) =>
        sourceLength == 0 ? 0 : sourceLength + HeaderSize;

    /// <summary>
    /// Compresses <paramref name="source"/> into <paramref name="destination"/> in the
    /// format described on this class.
    /// </summary>
    /// <returns>
    /// The number of bytes written, or -1 when <paramref name="destination"/> was too
    /// small, which is the caller's signal to fall back to <see cref="CodecId.Store"/>.
    /// </returns>
    public int Encode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty)
            return 0;
        if (destination.Length < HeaderSize)
            return -1;

        Span<int> frequencies = stackalloc int[SymbolCount];
        frequencies.Clear();

        foreach (byte value in source)
            frequencies[value]++;

        Span<byte> lengths = stackalloc byte[SymbolCount];
        CanonicalHuffman.BuildLengths(frequencies, lengths, MaxCodeLength);

        Span<uint> codes = stackalloc uint[SymbolCount];
        CanonicalHuffman.AssignCodes(lengths, codes, MaxCodeLength);

        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)source.Length);

        for (int i = 0; i < LengthTableSize; i++)
            destination[sizeof(uint) + i] = (byte)(lengths[2 * i] | lengths[2 * i + 1] << 4);

        var writer = new BitWriter(destination[HeaderSize..]);

        foreach (byte value in source)
            writer.WriteBits(codes[value], lengths[value]);

        int payload = writer.Flush();
        return payload < 0 ? -1 : HeaderSize + payload;
    }

    /// <summary>
    /// Decodes through a two-level lookup table: one index per symbol for codes up to
    /// 10 bits, two for the longer and much rarer ones.
    /// </summary>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="InvalidDataException">The input is malformed or truncated.</exception>
    public int Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty)
            return 0;

        Span<byte> lengths = stackalloc byte[SymbolCount];
        int count = ReadHeader(source, destination.Length, lengths);

        // About 20 KiB, preferred over ArrayPool: the size is a small constant unrelated
        // to the input, nothing recurses here, and a malformed stream can throw at any
        // point with no rented array to return. The table is rebuilt per call regardless.
        Span<uint> table = stackalloc uint[DecodeTableSize];
        CanonicalHuffman.BuildDecodeTable(lengths, table, MaxCodeLength, RootBits);

        var reader = new BitReader(source[HeaderSize..]);

        for (int i = 0; i < count; i++)
        {
            uint entry = table[(int)reader.PeekBits(RootBits)];

            if ((entry & CanonicalHuffman.LinkFlag) != 0)
            {
                reader.SkipBits(RootBits);

                int subtable = (int)((entry & ~CanonicalHuffman.LinkFlag) >> CanonicalHuffman.EntryFieldBits);
                int subtableBits = (int)(entry & CanonicalHuffman.EntryFieldMask);
                entry = table[subtable + (int)reader.PeekBits(subtableBits)];
            }

            if (entry == 0)
                throw new InvalidDataException("Invalid code in Huffman stream.");

            reader.SkipBits((int)(entry & CanonicalHuffman.EntryFieldMask));
            destination[i] = (byte)(entry >> CanonicalHuffman.EntryFieldBits);
        }

        if (reader.IsOverrun)
            throw new InvalidDataException("Truncated Huffman stream.");

        return count;
    }

    /// <summary>
    /// Decodes one bit at a time by walking the code tree. Same result as
    /// <see cref="Decode"/>; kept as the benchmark baseline for the lookup table.
    /// </summary>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="InvalidDataException">The input is malformed or truncated.</exception>
    public static int DecodeTreeWalk(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty)
            return 0;

        Span<byte> lengths = stackalloc byte[SymbolCount];
        int count = ReadHeader(source, destination.Length, lengths);

        // Two child slots per internal node: 0 is absent, a positive value is a node
        // index and a negative value is the complement of a symbol.
        Span<int> children = stackalloc int[2 * SymbolCount];
        CanonicalHuffman.BuildTree(lengths, children, MaxCodeLength);

        var reader = new BitReader(source[HeaderSize..]);

        for (int i = 0; i < count; i++)
        {
            int node = 0;

            do
            {
                node = children[2 * node + (reader.ReadBit() ? 1 : 0)];

                if (node == 0)
                    throw new InvalidDataException("Invalid code in Huffman stream.");
            }
            while (node > 0);

            destination[i] = (byte)~node;
        }

        if (reader.IsOverrun)
            throw new InvalidDataException("Truncated Huffman stream.");

        return count;
    }

    /// <summary>
    /// Validates the header and returns the symbol count. Everything here comes from
    /// untrusted input, and the table builders rely on the lengths being a valid code.
    /// </summary>
    private static int ReadHeader(ReadOnlySpan<byte> source, int destinationLength, Span<byte> lengths)
    {
        if (source.Length < HeaderSize)
            throw new InvalidDataException("Truncated Huffman header.");

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(source);

        if (count == 0)
            throw new InvalidDataException("Huffman stream declares no symbols.");
        if (count > (uint)destinationLength)
            throw new InvalidDataException("Huffman stream expands past the declared block size.");

        // Every symbol takes at least one bit.
        if (count > (source.Length - HeaderSize) * 8L)
            throw new InvalidDataException("Truncated Huffman stream.");

        ReadOnlySpan<byte> table = source.Slice(sizeof(uint), LengthTableSize);
        int kraftSum = 0;
        int used = 0;

        for (int symbol = 0; symbol < SymbolCount; symbol++)
        {
            byte packed = table[symbol / 2];
            int length = symbol % 2 == 0 ? packed & 0x0F : packed >> 4;

            lengths[symbol] = (byte)length;

            if (length != 0)
            {
                used++;
                kraftSum += 1 << (MaxCodeLength - length);
            }
        }

        bool complete = kraftSum == 1 << MaxCodeLength;
        bool singleSymbol = used == 1 && kraftSum == 1 << (MaxCodeLength - 1);

        if (!complete && !singleSymbol)
            throw new InvalidDataException("Huffman code lengths do not form a valid prefix code.");

        return (int)count;
    }
}
