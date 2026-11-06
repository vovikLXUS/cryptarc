using System.Buffers;
using CryptArc.Core.Coding;
using CryptArc.Core.IO;

namespace CryptArc.Core.Codecs;

/// <summary>
/// Deflate: LZ77 matching followed by Huffman coding, exactly as RFC 1951 defines it.
/// </summary>
/// <remarks>
/// <para>
/// The stream is a sequence of blocks. Every block opens with a 3-bit header, written
/// least-significant-bit-first like everything else in <see cref="BitWriter"/>:
/// </para>
/// <list type="bullet">
///   <item><description><c>BFINAL</c> — 1 bit, set on the last block of the stream.</description></item>
///   <item><description>
///     <c>BTYPE</c> — 2 bits: 0 stored, 1 fixed Huffman codes, 2 dynamic Huffman codes,
///     3 reserved and always an error.
///   </description></item>
/// </list>
/// <para>
/// A stored block (<c>BTYPE = 0</c>) pads to the next byte boundary, then carries
/// <c>LEN</c> and <c>NLEN</c> as little-endian 16-bit values — <c>NLEN</c> is the one's
/// complement of <c>LEN</c>, which catches a desynchronised reader immediately — and then
/// <c>LEN</c> raw bytes. A block holds at most 65 535 bytes, so longer input becomes
/// several blocks.
/// </para>
/// <para>
/// A fixed block (<c>BTYPE = 1</c>) codes two interleaved alphabets with the code lengths
/// the specification fixes in advance, so nothing describing them is transmitted:
/// </para>
/// <list type="bullet">
///   <item><description>
///     Literal/length, 288 symbols: 0…255 are literal bytes, 256 ends the block, and
///     257…285 introduce a match length of 3…258. Lengths are 8 bits for symbols 0…143,
///     9 for 144…255, 7 for 256…279 and 8 for 280…287 (RFC 1951 §3.2.6).
///   </description></item>
///   <item><description>
///     Distance, 30 symbols of 5 bits each, introducing a distance of 1…32 768.
///   </description></item>
/// </list>
/// <para>
/// A length or distance symbol names a base value and how many extra bits follow
/// (§3.2.5). The extra bits are plain integers, written low bit first; the Huffman codes
/// themselves are bit-reversed by <see cref="CanonicalHuffman.AssignCodes"/> so that
/// writing them low bit first puts the code's first bit on the wire first, as the
/// specification requires.
/// </para>
/// <para>
/// <see cref="Encode"/> codes the whole input as one fixed block and keeps it only if it
/// came out smaller than the stored form; otherwise the input is stored verbatim. An
/// archive can therefore never grow by more than the stored framing.
/// </para>
/// <para>
/// Compatibility is deliberate and exact: the output is a raw deflate stream that any
/// RFC 1951 decoder reads, and <see cref="Decode"/> reads what they produce. That is what
/// lets the tests use a reference implementation as a correctness oracle — see ADR-0013.
/// There is no zlib or gzip wrapper: the container supplies the length and the checksum.
/// </para>
/// <para>
/// Complexity. Stored blocks are O(n) time, O(1) space. <see cref="Encode"/> is O(n·k)
/// time with k = <see cref="MaxChainLength"/>, plus 256 KiB of pooled scratch for the hash
/// chains; <see cref="EncodeNaive"/> is O(n·W) with W = 32 768. <see cref="Decode"/> is
/// O(n), with the decoding tables held as shared immutable statics because the fixed code
/// lengths never change.
/// </para>
/// <para>
/// Not implemented yet: dynamic blocks (<c>BTYPE = 2</c>), which <see cref="Decode"/>
/// rejects with <see cref="NotSupportedException"/>.
/// </para>
/// </remarks>
public sealed class DeflateCodec : ICodec
{
    /// <summary>Size of the sliding window, and so the largest distance a match can reach.</summary>
    public const int WindowSize = 1 << 15;

    /// <summary>The shortest match the format can express.</summary>
    public const int MinMatch = 3;

    /// <summary>The longest match the format can express.</summary>
    public const int MaxMatch = 258;

    /// <summary>The default for <see cref="MaxChainLength"/>, used by the registered instance.</summary>
    public const int DefaultMaxChainLength = 128;

    private const int MaxStoredBlock = 0xFFFF;
    private const int StoredBlockOverhead = 5;

    private const uint StoredBlock = 0;
    private const uint FixedBlock = 1;
    private const uint DynamicBlock = 2;

    private const int LiteralAlphabetSize = 288;
    private const int DistanceAlphabetSize = 30;
    private const int EndOfBlock = 256;
    private const int FirstLengthSymbol = 257;

    private const int FixedLiteralBits = 9;
    private const int FixedDistanceBits = 5;

    /// <summary>Smallest match length each length symbol 257… stands for (RFC 1951 §3.2.5).</summary>
    private static readonly ushort[] LengthBase =
    [
        3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31,
        35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258,
    ];

    /// <summary>Extra bits that follow each length symbol.</summary>
    private static readonly byte[] LengthExtra =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
        3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0,
    ];

    /// <summary>Smallest distance each distance symbol stands for.</summary>
    private static readonly ushort[] DistanceBase =
    [
        1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193,
        257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577,
    ];

    /// <summary>Extra bits that follow each distance symbol.</summary>
    private static readonly byte[] DistanceExtra =
    [
        0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
        7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13,
    ];

    private static readonly byte[] FixedLiteralLengths = BuildFixedLiteralLengths();
    private static readonly byte[] FixedDistanceLengths = BuildFixedDistanceLengths();

    private static readonly uint[] FixedLiteralCodes = BuildCodes(FixedLiteralLengths, FixedLiteralBits);
    private static readonly uint[] FixedDistanceCodes = BuildCodes(FixedDistanceLengths, FixedDistanceBits);

    private static readonly uint[] FixedLiteralTable = BuildTable(FixedLiteralLengths, FixedLiteralBits);
    private static readonly uint[] FixedDistanceTable = BuildTable(FixedDistanceLengths, FixedDistanceBits);

    private static readonly byte[] LengthSymbolOf = BuildLengthSymbolOf();
    private static readonly byte[] DistanceSymbolOf = BuildDistanceSymbolOf();

    /// <summary>Initializes a new instance of the <see cref="DeflateCodec"/> class.</summary>
    /// <param name="maxChainLength">
    /// The largest number of earlier positions the encoder compares for one match. Larger
    /// values find longer matches more slowly. Use <see cref="int.MaxValue"/> to search the
    /// whole window, which makes <see cref="Encode"/> agree byte for byte with
    /// <see cref="EncodeNaive"/> when lazy matching is off.
    /// </param>
    /// <param name="lazyMatching">
    /// <see langword="true"/> to look one byte ahead before emitting a match; otherwise,
    /// <see langword="false"/> to take every match greedily.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxChainLength"/> is less than 1.</exception>
    public DeflateCodec(int maxChainLength = DefaultMaxChainLength, bool lazyMatching = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChainLength, 1);

        MaxChainLength = maxChainLength;
        LazyMatching = lazyMatching;
    }

    /// <summary>Gets the largest number of earlier positions compared for one match.</summary>
    /// <value>The chain limit. The default is <see cref="DefaultMaxChainLength"/>.</value>
    public int MaxChainLength { get; }

    /// <summary>Gets a value that indicates whether the encoder looks one byte ahead before emitting a match.</summary>
    /// <value><see langword="true"/> if lazy matching is on; otherwise, <see langword="false"/>.</value>
    public bool LazyMatching { get; }

    /// <inheritdoc/>
    public CodecId Id => CodecId.Deflate;

    /// <inheritdoc/>
    public string Name => "Deflate";

    /// <inheritdoc/>
    /// <remarks>
    /// Sized for the stored fallback, which is what incompressible input ends up as. A
    /// Huffman block is kept only when it came out smaller, so this stays an upper bound.
    /// </remarks>
    public int MaxCompressedSize(int sourceLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceLength);

        return StoredLength(sourceLength);
    }

    /// <inheritdoc/>
    /// <remarks>Searches for matches with hash chains. See the class remarks.</remarks>
    public int Encode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int[] head = ArrayPool<int>.Shared.Rent(HashChainFinder.HashSize);
        try
        {
            int[] prev = ArrayPool<int>.Shared.Rent(WindowSize);
            try
            {
                head.AsSpan(0, HashChainFinder.HashSize).Fill(HashChainFinder.NoPosition);
                var finder = new HashChainFinder(head, prev, WindowSize, MinMatch, MaxMatch, MaxChainLength);

                int fixedLength = EncodeFixed(source, destination, ref finder, LazyMatching);
                if (IsWorthKeeping(fixedLength, source.Length))
                    return fixedLength;
            }
            finally
            {
                ArrayPool<int>.Shared.Return(prev);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(head);
        }

        return EncodeStored(source, destination);
    }

    /// <summary>
    /// Compresses <paramref name="source"/> with a greedy linear scan of the whole window.
    /// Kept as the benchmark baseline and as a correctness oracle for <see cref="Encode"/>.
    /// </summary>
    /// <param name="source">The bytes to compress.</param>
    /// <param name="destination">The buffer that receives the compressed stream.</param>
    /// <returns>The number of bytes written, or -1 when <paramref name="destination"/> is too small.</returns>
    public static int EncodeNaive(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var finder = new LinearScanFinder(WindowSize, MinMatch, MaxMatch);

        int fixedLength = EncodeFixed(source, destination, ref finder, lazy: false);
        return IsWorthKeeping(fixedLength, source.Length)
            ? fixedLength
            : EncodeStored(source, destination);
    }

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">
    /// The stream contains a dynamic Huffman block, which is not implemented yet.
    /// </exception>
    public int Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty)
            return 0;

        var reader = new BitReader(source);
        int written = 0;
        bool final;

        do
        {
            final = reader.ReadBit();
            uint type = reader.ReadBits(2);

            if (reader.IsOverrun)
                throw Truncated();

            switch (type)
            {
                case StoredBlock:
                    ReadStoredBlock(ref reader, destination, ref written);
                    break;

                case FixedBlock:
                    ReadFixedBlock(ref reader, destination, ref written);
                    break;

                case DynamicBlock:
                    throw new NotSupportedException("Deflate dynamic Huffman blocks are not implemented yet.");

                default:
                    throw new InvalidDataException("Deflate block type 3 is reserved.");
            }
        }
        while (!final);

        return written;
    }

    /// <summary>
    /// Codes the whole input as one fixed Huffman block. The parser shared by both
    /// encoders: literals are emitted one symbol at a time, a match when
    /// <typeparamref name="TFinder"/> finds one, unless lazy matching finds a longer one at
    /// the next position.
    /// </summary>
    private static int EncodeFixed<TFinder>(
        ReadOnlySpan<byte> source, Span<byte> destination, ref TFinder finder, bool lazy)
        where TFinder : struct, IMatchFinder
    {
        var writer = new BitWriter(destination);

        writer.WriteBits(1, 1);
        writer.WriteBits(FixedBlock, 2);

        int position = 0;
        Match match = finder.Find(source, position);

        while (position < source.Length)
        {
            if (writer.Overflowed)
                return -1;

            if (match.Length < MinMatch)
            {
                WriteSymbol(ref writer, source[position]);
                position++;
                match = finder.Find(source, position);
                continue;
            }

            if (lazy && match.Length < MaxMatch)
            {
                Match next = finder.Find(source, position + 1);
                if (next.Length > match.Length)
                {
                    // Emit the current byte as a literal and take the longer match next time round.
                    WriteSymbol(ref writer, source[position]);
                    position++;
                    match = next;
                    continue;
                }
            }

            WriteMatch(ref writer, match);
            position += match.Length;
            match = finder.Find(source, position);
        }

        WriteSymbol(ref writer, EndOfBlock);
        return writer.Flush();
    }

    /// <summary>Emits the input as stored blocks, which is the incompressible fallback.</summary>
    private static int EncodeStored(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var writer = new BitWriter(destination);
        int offset = 0;

        do
        {
            int length = Math.Min(MaxStoredBlock, source.Length - offset);
            WriteStoredBlock(ref writer, source.Slice(offset, length), final: offset + length >= source.Length);
            offset += length;
        }
        while (offset < source.Length);

        return writer.Flush();
    }

    /// <summary>Writes one literal/length-alphabet symbol with its fixed code.</summary>
    private static void WriteSymbol(ref BitWriter writer, int symbol) =>
        writer.WriteBits(FixedLiteralCodes[symbol], FixedLiteralLengths[symbol]);

    /// <summary>Writes a match as a length symbol with extra bits, then a distance symbol with extra bits.</summary>
    private static void WriteMatch(ref BitWriter writer, Match match)
    {
        int lengthSymbol = LengthSymbolOf[match.Length];
        WriteSymbol(ref writer, FirstLengthSymbol + lengthSymbol);

        int lengthExtra = LengthExtra[lengthSymbol];
        if (lengthExtra != 0)
            writer.WriteBits((uint)(match.Length - LengthBase[lengthSymbol]), lengthExtra);

        int distanceSymbol = DistanceSymbolOf[match.Distance];
        writer.WriteBits(FixedDistanceCodes[distanceSymbol], FixedDistanceLengths[distanceSymbol]);

        int distanceExtra = DistanceExtra[distanceSymbol];
        if (distanceExtra != 0)
            writer.WriteBits((uint)(match.Distance - DistanceBase[distanceSymbol]), distanceExtra);
    }

    /// <summary>Emits one stored block: the header, the byte-aligned lengths, then the payload verbatim.</summary>
    private static void WriteStoredBlock(ref BitWriter writer, ReadOnlySpan<byte> block, bool final)
    {
        writer.WriteBits(final ? 1u : 0u, 1);
        writer.WriteBits(StoredBlock, 2);
        writer.AlignToByte();

        writer.WriteBits((uint)block.Length, 16);
        writer.WriteBits((uint)(~block.Length & MaxStoredBlock), 16);

        // Per byte rather than a bulk copy: this is the incompressible fallback, not a
        // hot path, and it keeps BitWriter's overflow accounting in one place.
        foreach (byte value in block)
            writer.WriteBits(value, 8);
    }

    /// <summary>Reads one stored block, appending it at <paramref name="written"/>.</summary>
    private static void ReadStoredBlock(ref BitReader reader, Span<byte> destination, ref int written)
    {
        reader.AlignToByte();

        int length = (int)reader.ReadBits(16);
        int complement = (int)reader.ReadBits(16);

        if (reader.IsOverrun)
            throw Truncated();
        if ((~length & MaxStoredBlock) != complement)
            throw new InvalidDataException("Deflate stored block length does not match its complement.");
        if (length > destination.Length - written)
            throw ExpandsPastBlock();

        for (int i = 0; i < length; i++)
            destination[written + i] = (byte)reader.ReadBits(8);

        if (reader.IsOverrun)
            throw Truncated();

        written += length;
    }

    /// <summary>Reads one fixed Huffman block, appending it at <paramref name="written"/>.</summary>
    private static void ReadFixedBlock(ref BitReader reader, Span<byte> destination, ref int written)
    {
        while (true)
        {
            int symbol = DecodeSymbol(ref reader, FixedLiteralTable, FixedLiteralBits);

            if (reader.IsOverrun)
                throw Truncated();

            if (symbol < EndOfBlock)
            {
                if (written >= destination.Length)
                    throw ExpandsPastBlock();

                destination[written++] = (byte)symbol;
                continue;
            }

            if (symbol == EndOfBlock)
                return;

            int lengthSymbol = symbol - FirstLengthSymbol;

            if (lengthSymbol >= LengthBase.Length)
                throw new InvalidDataException($"Deflate length symbol {symbol} is invalid.");

            int length = LengthBase[lengthSymbol] + (int)reader.ReadBits(LengthExtra[lengthSymbol]);
            int distanceSymbol = DecodeSymbol(ref reader, FixedDistanceTable, FixedDistanceBits);

            if (distanceSymbol >= DistanceBase.Length)
                throw new InvalidDataException($"Deflate distance symbol {distanceSymbol} is invalid.");

            int distance = DistanceBase[distanceSymbol] + (int)reader.ReadBits(DistanceExtra[distanceSymbol]);

            if (reader.IsOverrun)
                throw Truncated();
            if (distance > written)
                throw new InvalidDataException("Deflate match points before the start of the stream.");
            if (length > destination.Length - written)
                throw ExpandsPastBlock();

            LzOutput.CopyMatch(destination, written, distance, length);
            written += length;
        }
    }

    /// <summary>Reads one symbol through a two-level lookup table.</summary>
    private static int DecodeSymbol(ref BitReader reader, ReadOnlySpan<uint> table, int rootBits)
    {
        uint entry = table[(int)reader.PeekBits(rootBits)];

        if ((entry & CanonicalHuffman.LinkFlag) != 0)
        {
            reader.SkipBits(rootBits);

            int subtable = (int)((entry & ~CanonicalHuffman.LinkFlag) >> CanonicalHuffman.EntryFieldBits);
            int subtableBits = (int)(entry & CanonicalHuffman.EntryFieldMask);
            entry = table[subtable + (int)reader.PeekBits(subtableBits)];
        }

        if (entry == 0)
            throw new InvalidDataException("Invalid code in Deflate stream.");

        reader.SkipBits((int)(entry & CanonicalHuffman.EntryFieldMask));
        return (int)(entry >> CanonicalHuffman.EntryFieldBits);
    }

    /// <summary>Exact size of the stored form, which is also the worst case for the whole codec.</summary>
    private static int StoredLength(int sourceLength)
    {
        long blocks = Math.Max(1, (sourceLength + MaxStoredBlock - 1) / MaxStoredBlock);
        long bound = sourceLength + blocks * StoredBlockOverhead;

        return bound > int.MaxValue ? int.MaxValue : (int)bound;
    }

    /// <summary>A Huffman block is kept only when it fit and beat storing the input verbatim.</summary>
    private static bool IsWorthKeeping(int compressedLength, int sourceLength) =>
        compressedLength >= 0 && compressedLength < StoredLength(sourceLength);

    private static byte[] BuildFixedLiteralLengths()
    {
        byte[] lengths = new byte[LiteralAlphabetSize];

        for (int symbol = 0; symbol < LiteralAlphabetSize; symbol++)
        {
            lengths[symbol] = symbol switch
            {
                <= 143 => 8,
                <= 255 => 9,
                <= 279 => 7,
                _ => 8,
            };
        }

        return lengths;
    }

    private static byte[] BuildFixedDistanceLengths()
    {
        byte[] lengths = new byte[DistanceAlphabetSize];
        lengths.AsSpan().Fill(FixedDistanceBits);
        return lengths;
    }

    private static uint[] BuildCodes(byte[] lengths, int maxCodeLength)
    {
        uint[] codes = new uint[lengths.Length];
        CanonicalHuffman.AssignCodes(lengths, codes, maxCodeLength);
        return codes;
    }

    private static uint[] BuildTable(byte[] lengths, int maxCodeLength)
    {
        // Root bits equal to the longest code: the fixed alphabets are small enough that
        // one lookup always suffices, so there are no subtables to walk.
        uint[] table = new uint[CanonicalHuffman.DecodeTableSize(lengths.Length, maxCodeLength, maxCodeLength)];
        CanonicalHuffman.BuildDecodeTable(lengths, table, maxCodeLength, maxCodeLength);
        return table;
    }

    /// <summary>Maps a match length of 3…258 onto the length symbol that covers it.</summary>
    private static byte[] BuildLengthSymbolOf()
    {
        byte[] map = new byte[MaxMatch + 1];

        for (int symbol = 0; symbol < LengthBase.Length; symbol++)
        {
            int last = symbol + 1 < LengthBase.Length ? LengthBase[symbol + 1] - 1 : MaxMatch;

            for (int length = LengthBase[symbol]; length <= last; length++)
                map[length] = (byte)symbol;
        }

        return map;
    }

    /// <summary>Maps a distance of 1…32 768 onto the distance symbol that covers it.</summary>
    private static byte[] BuildDistanceSymbolOf()
    {
        byte[] map = new byte[WindowSize + 1];

        for (int symbol = 0; symbol < DistanceBase.Length; symbol++)
        {
            int last = symbol + 1 < DistanceBase.Length ? DistanceBase[symbol + 1] - 1 : WindowSize;

            for (int distance = DistanceBase[symbol]; distance <= last; distance++)
                map[distance] = (byte)symbol;
        }

        return map;
    }

    private static InvalidDataException Truncated() =>
        new("Truncated Deflate stream.");

    private static InvalidDataException ExpandsPastBlock() =>
        new("Deflate stream expands past the declared block size.");
}
