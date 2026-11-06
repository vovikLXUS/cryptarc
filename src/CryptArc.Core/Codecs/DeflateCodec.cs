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
/// A stored block pads to the next byte boundary, then carries <c>LEN</c> and <c>NLEN</c>
/// as little-endian 16-bit values — <c>NLEN</c> is the one's complement of <c>LEN</c>,
/// which catches a desynchronised reader immediately — and then <c>LEN</c> raw bytes. A
/// block holds at most 65 535 bytes, so longer input becomes several blocks.
/// </para>
/// <para>
/// The two Huffman block types code the same token stream — literals, and matches as a
/// length plus a distance — over two interleaved alphabets:
/// </para>
/// <list type="bullet">
///   <item><description>
///     Literal/length, up to 288 symbols: 0…255 are literal bytes, 256 ends the block, and
///     257…285 introduce a match length of 3…258.
///   </description></item>
///   <item><description>
///     Distance, up to 30 symbols, introducing a distance of 1…32 768.
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
/// A fixed block (<c>BTYPE = 1</c>) uses the code lengths the specification fixes in
/// advance, so nothing describing them is transmitted: 8 bits for literal/length symbols
/// 0…143, 9 for 144…255, 7 for 256…279, 8 for 280…287, and 5 bits for every distance
/// symbol (§3.2.6).
/// </para>
/// <para>
/// A dynamic block (<c>BTYPE = 2</c>) fits the codes to the block and then has to describe
/// them (§3.2.7). It transmits <c>HLIT</c>, <c>HDIST</c> and <c>HCLEN</c> — how many
/// literal/length, distance and code-length lengths follow — then the code lengths of a
/// third, 19-symbol alphabet as 3-bit values in the specification's permuted order
/// (16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15, so that the rarely
/// used entries fall at the end and can be omitted), and finally the lengths of the first
/// two alphabets coded with that third one. In that coding, 0…15 is a length, 16 repeats
/// the previous length 3…6 times, 17 runs 3…10 zeros and 18 runs 11…138 zeros. The repeat
/// codes run across the boundary between the two alphabets, which are described as one
/// sequence.
/// </para>
/// <para>
/// <see cref="Encode"/> parses the input once into a token buffer, then prices all three
/// block types in bits — the fixed and dynamic costs are dot products of the symbol
/// frequencies with the respective code lengths, and the dynamic one adds the cost of
/// describing its own codes — and emits the cheapest. Incompressible input therefore still
/// ends up stored, and an archive can never grow by more than the stored framing.
/// </para>
/// <para>
/// Compatibility is deliberate and exact: the output is a raw deflate stream that any
/// RFC 1951 decoder reads, and <see cref="Decode"/> reads what they produce. That is what
/// lets the tests use a reference implementation as a correctness oracle — see ADR-0013.
/// There is no zlib or gzip wrapper: the container supplies the length and the checksum.
/// </para>
/// <para>
/// Complexity. <see cref="Encode"/> is O(n·k) time with k = <see cref="MaxChainLength"/>,
/// and allocates from <see cref="ArrayPool{T}"/>: 256 KiB for the hash chains plus 4 bytes
/// per input byte for the token buffer. <see cref="EncodeNaive"/> is O(n·W) with
/// W = 32 768. <see cref="Decode"/> is O(n), with the fixed tables held as shared
/// immutable statics and the dynamic ones rebuilt per block into pooled scratch.
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
    private const int BlockHeaderBits = 3;

    private const uint StoredBlock = 0;
    private const uint FixedBlock = 1;
    private const uint DynamicBlock = 2;

    private const int LiteralAlphabetSize = 288;
    private const int DistanceAlphabetSize = 30;
    private const int CodeLengthAlphabetSize = 19;

    /// <summary>Most literal/length lengths a dynamic block may transmit: HLIT is 5 bits.</summary>
    private const int MaxLiteralCodes = 286;

    /// <summary>Fewest code-length lengths a dynamic block may transmit: HCLEN is 4 bits.</summary>
    private const int MinCodeLengthCodes = 4;

    private const int EndOfBlock = 256;
    private const int FirstLengthSymbol = 257;

    private const int MaxCodeLength = 15;
    private const int CodeLengthBits = 7;
    private const int FixedLiteralBits = 9;
    private const int FixedDistanceBits = 5;
    private const int DynamicRootBits = 10;

    private const int RepeatPrevious = 16;
    private const int RepeatZeroShort = 17;
    private const int RepeatZeroLong = 18;

    private const uint MatchToken = 0x8000_0000;
    private const int TokenDistanceShift = 9;
    private const uint TokenLengthMask = (1u << TokenDistanceShift) - 1;

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

    /// <summary>
    /// The order a dynamic block transmits the code-length alphabet's own lengths in
    /// (RFC 1951 §3.2.7). Rarely used entries come last so HCLEN can cut them off.
    /// </summary>
    private static readonly byte[] CodeLengthOrder =
    [
        16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15,
    ];

    /// <summary>Extra bits that follow each code-length symbol: the three repeat codes carry a count.</summary>
    private static readonly byte[] CodeLengthExtra =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 3, 7,
    ];

    private static readonly byte[] FixedLiteralLengths = BuildFixedLiteralLengths();
    private static readonly byte[] FixedDistanceLengths = BuildFixedDistanceLengths();

    private static readonly uint[] FixedLiteralCodes = BuildCodes(FixedLiteralLengths, FixedLiteralBits);
    private static readonly uint[] FixedDistanceCodes = BuildCodes(FixedDistanceLengths, FixedDistanceBits);

    private static readonly uint[] FixedLiteralTable = BuildTable(FixedLiteralLengths, FixedLiteralBits);
    private static readonly uint[] FixedDistanceTable = BuildTable(FixedDistanceLengths, FixedDistanceBits);

    private static readonly byte[] LengthSymbolOf = BuildLengthSymbolOf();
    private static readonly byte[] DistanceSymbolOf = BuildDistanceSymbolOf();

    private static readonly int DynamicLiteralTableSize =
        CanonicalHuffman.DecodeTableSize(LiteralAlphabetSize, MaxCodeLength, DynamicRootBits);

    private static readonly int DynamicDistanceTableSize =
        CanonicalHuffman.DecodeTableSize(DistanceAlphabetSize, MaxCodeLength, DynamicRootBits);

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
    /// Huffman block is emitted only when it was priced below that, so this stays an upper
    /// bound.
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
                uint[] tokens = ArrayPool<uint>.Shared.Rent(Math.Max(1, source.Length));
                try
                {
                    head.AsSpan(0, HashChainFinder.HashSize).Fill(HashChainFinder.NoPosition);
                    var finder = new HashChainFinder(head, prev, WindowSize, MinMatch, MaxMatch, MaxChainLength);

                    return EncodeCore(source, destination, ref finder, LazyMatching, tokens);
                }
                finally
                {
                    ArrayPool<uint>.Shared.Return(tokens);
                }
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
        uint[] tokens = ArrayPool<uint>.Shared.Rent(Math.Max(1, source.Length));
        try
        {
            var finder = new LinearScanFinder(WindowSize, MinMatch, MaxMatch);
            return EncodeCore(source, destination, ref finder, lazy: false, tokens);
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(tokens);
        }
    }

    /// <inheritdoc/>
    public int Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty)
            return 0;

        // Rented rather than stack-allocated: a dynamic block needs about 28 KiB of table,
        // and a stream may hold many blocks. Allocating per block on the stack would never
        // be released until Decode returned.
        uint[] literalTable = ArrayPool<uint>.Shared.Rent(DynamicLiteralTableSize);
        try
        {
            uint[] distanceTable = ArrayPool<uint>.Shared.Rent(DynamicDistanceTableSize);
            try
            {
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
                            ReadHuffmanBlock(
                                ref reader, destination, ref written,
                                FixedLiteralTable, FixedLiteralBits,
                                FixedDistanceTable, FixedDistanceBits);
                            break;

                        case DynamicBlock:
                            ReadDynamicBlock(
                                ref reader, destination, ref written,
                                literalTable.AsSpan(0, DynamicLiteralTableSize),
                                distanceTable.AsSpan(0, DynamicDistanceTableSize));
                            break;

                        default:
                            throw new InvalidDataException("Deflate block type 3 is reserved.");
                    }
                }
                while (!final);

                return written;
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(distanceTable);
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(literalTable);
        }
    }

    /// <summary>
    /// Parses the input into tokens once, prices the three block types, and emits the
    /// cheapest. Shared by both encoders; <typeparamref name="TFinder"/> is what they
    /// differ in.
    /// </summary>
    private static int EncodeCore<TFinder>(
        ReadOnlySpan<byte> source, Span<byte> destination, ref TFinder finder, bool lazy, Span<uint> tokens)
        where TFinder : struct, IMatchFinder
    {
        int storedLength = StoredLength(source.Length);

        Span<int> literalFrequencies = stackalloc int[LiteralAlphabetSize];
        Span<int> distanceFrequencies = stackalloc int[DistanceAlphabetSize];
        literalFrequencies.Clear();
        distanceFrequencies.Clear();

        int tokenCount = Parse(
            source, ref finder, lazy, tokens, literalFrequencies, distanceFrequencies, out long extraBits);
        literalFrequencies[EndOfBlock]++;

        // A fixed block's code lengths are given, so its cost is a dot product.
        long fixedBits = BlockHeaderBits + extraBits
            + SymbolBits(literalFrequencies, FixedLiteralLengths)
            + SymbolBits(distanceFrequencies, FixedDistanceLengths);

        // The specification wants at least two codes in every alphabet, so that a tree is
        // never a single zero-length code and every alphabet is a complete prefix code.
        EnsureTwoSymbols(literalFrequencies);
        EnsureTwoSymbols(distanceFrequencies);

        Span<byte> literalLengths = stackalloc byte[LiteralAlphabetSize];
        Span<byte> distanceLengths = stackalloc byte[DistanceAlphabetSize];
        CanonicalHuffman.BuildLengths(literalFrequencies, literalLengths, MaxCodeLength);
        CanonicalHuffman.BuildLengths(distanceFrequencies, distanceLengths, MaxCodeLength);

        int hlit = TransmittedCount(literalLengths, FirstLengthSymbol, MaxLiteralCodes);
        int hdist = TransmittedCount(distanceLengths, 1, DistanceAlphabetSize);

        // The two alphabets are described as one sequence: a repeat code may run across
        // the boundary between them.
        Span<byte> sequence = stackalloc byte[MaxLiteralCodes + DistanceAlphabetSize];
        literalLengths[..hlit].CopyTo(sequence);
        distanceLengths[..hdist].CopyTo(sequence[hlit..]);

        Span<byte> runSymbols = stackalloc byte[MaxLiteralCodes + DistanceAlphabetSize];
        Span<byte> runExtras = stackalloc byte[MaxLiteralCodes + DistanceAlphabetSize];
        int runCount = PackCodeLengths(sequence[..(hlit + hdist)], runSymbols, runExtras);

        Span<int> codeLengthFrequencies = stackalloc int[CodeLengthAlphabetSize];
        codeLengthFrequencies.Clear();

        for (int i = 0; i < runCount; i++)
            codeLengthFrequencies[runSymbols[i]]++;

        EnsureTwoSymbols(codeLengthFrequencies);

        Span<byte> codeLengthLengths = stackalloc byte[CodeLengthAlphabetSize];
        CanonicalHuffman.BuildLengths(codeLengthFrequencies, codeLengthLengths, CodeLengthBits);

        int hclen = TransmittedCountInOrder(codeLengthLengths);

        // Describing the codes is what a dynamic block pays for the better fit. The
        // frequencies here include the one or two artificial counts EnsureTwoSymbols added,
        // which overstates the cost by at most a couple of bits — only ever conservative.
        long dynamicBits = BlockHeaderBits + extraBits + 5 + 5 + 4 + (3L * hclen)
            + SymbolBits(literalFrequencies, literalLengths)
            + SymbolBits(distanceFrequencies, distanceLengths);

        for (int i = 0; i < runCount; i++)
            dynamicBits += codeLengthLengths[runSymbols[i]] + CodeLengthExtra[runSymbols[i]];

        bool dynamicWins = dynamicBits <= fixedBits;
        long huffmanBits = dynamicWins ? dynamicBits : fixedBits;

        if ((huffmanBits + 7) / 8 >= storedLength)
            return EncodeStored(source, destination);

        var writer = new BitWriter(destination);
        writer.WriteBits(1, 1);

        if (dynamicWins)
        {
            writer.WriteBits(DynamicBlock, 2);
            WriteDynamicHeader(
                ref writer, hlit, hdist, hclen, runSymbols[..runCount], runExtras[..runCount], codeLengthLengths);

            Span<uint> literalCodes = stackalloc uint[LiteralAlphabetSize];
            Span<uint> distanceCodes = stackalloc uint[DistanceAlphabetSize];
            CanonicalHuffman.AssignCodes(literalLengths, literalCodes, MaxCodeLength);
            CanonicalHuffman.AssignCodes(distanceLengths, distanceCodes, MaxCodeLength);

            WriteTokens(
                ref writer, tokens[..tokenCount], literalCodes, literalLengths, distanceCodes, distanceLengths);
        }
        else
        {
            writer.WriteBits(FixedBlock, 2);
            WriteTokens(
                ref writer, tokens[..tokenCount],
                FixedLiteralCodes, FixedLiteralLengths, FixedDistanceCodes, FixedDistanceLengths);
        }

        return writer.Flush();
    }

    /// <summary>
    /// Turns the input into literal and match tokens, counting what each alphabet needs.
    /// A match is taken when the finder reports one, unless lazy matching finds a longer
    /// one at the next position.
    /// </summary>
    /// <returns>How many tokens were written to <paramref name="tokens"/>.</returns>
    private static int Parse<TFinder>(
        ReadOnlySpan<byte> source,
        ref TFinder finder,
        bool lazy,
        Span<uint> tokens,
        Span<int> literalFrequencies,
        Span<int> distanceFrequencies,
        out long extraBits)
        where TFinder : struct, IMatchFinder
    {
        extraBits = 0;
        int count = 0;
        int position = 0;
        Match match = finder.Find(source, position);

        while (position < source.Length)
        {
            if (match.Length < MinMatch)
            {
                count = TakeLiteral(source, position, tokens, literalFrequencies, count);
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
                    count = TakeLiteral(source, position, tokens, literalFrequencies, count);
                    position++;
                    match = next;
                    continue;
                }
            }

            int lengthSymbol = LengthSymbolOf[match.Length];
            int distanceSymbol = DistanceSymbolOf[match.Distance];

            tokens[count++] = MatchToken | ((uint)match.Distance << TokenDistanceShift) | (uint)match.Length;
            literalFrequencies[FirstLengthSymbol + lengthSymbol]++;
            distanceFrequencies[distanceSymbol]++;
            extraBits += LengthExtra[lengthSymbol] + DistanceExtra[distanceSymbol];

            position += match.Length;
            match = finder.Find(source, position);
        }

        return count;
    }

    private static int TakeLiteral(
        ReadOnlySpan<byte> source, int position, Span<uint> tokens, Span<int> literalFrequencies, int count)
    {
        byte literal = source[position];
        tokens[count] = literal;
        literalFrequencies[literal]++;
        return count + 1;
    }

    /// <summary>Writes the token stream with the given codes, then the end-of-block symbol.</summary>
    private static void WriteTokens(
        ref BitWriter writer,
        scoped ReadOnlySpan<uint> tokens,
        scoped ReadOnlySpan<uint> literalCodes,
        scoped ReadOnlySpan<byte> literalLengths,
        scoped ReadOnlySpan<uint> distanceCodes,
        scoped ReadOnlySpan<byte> distanceLengths)
    {
        foreach (uint token in tokens)
        {
            if ((token & MatchToken) == 0)
            {
                writer.WriteBits(literalCodes[(int)token], literalLengths[(int)token]);
                continue;
            }

            int length = (int)(token & TokenLengthMask);
            int distance = (int)((token & ~MatchToken) >> TokenDistanceShift);

            int lengthSymbol = LengthSymbolOf[length];
            int symbol = FirstLengthSymbol + lengthSymbol;
            writer.WriteBits(literalCodes[symbol], literalLengths[symbol]);

            if (LengthExtra[lengthSymbol] != 0)
                writer.WriteBits((uint)(length - LengthBase[lengthSymbol]), LengthExtra[lengthSymbol]);

            int distanceSymbol = DistanceSymbolOf[distance];
            writer.WriteBits(distanceCodes[distanceSymbol], distanceLengths[distanceSymbol]);

            if (DistanceExtra[distanceSymbol] != 0)
                writer.WriteBits((uint)(distance - DistanceBase[distanceSymbol]), DistanceExtra[distanceSymbol]);
        }

        writer.WriteBits(literalCodes[EndOfBlock], literalLengths[EndOfBlock]);
    }

    /// <summary>Writes a dynamic block's three-alphabet description (RFC 1951 §3.2.7).</summary>
    private static void WriteDynamicHeader(
        ref BitWriter writer,
        int hlit,
        int hdist,
        int hclen,
        scoped ReadOnlySpan<byte> runSymbols,
        scoped ReadOnlySpan<byte> runExtras,
        scoped ReadOnlySpan<byte> codeLengthLengths)
    {
        writer.WriteBits((uint)(hlit - FirstLengthSymbol), 5);
        writer.WriteBits((uint)(hdist - 1), 5);
        writer.WriteBits((uint)(hclen - MinCodeLengthCodes), 4);

        for (int i = 0; i < hclen; i++)
            writer.WriteBits(codeLengthLengths[CodeLengthOrder[i]], 3);

        Span<uint> codes = stackalloc uint[CodeLengthAlphabetSize];
        CanonicalHuffman.AssignCodes(codeLengthLengths, codes, CodeLengthBits);

        for (int i = 0; i < runSymbols.Length; i++)
        {
            int symbol = runSymbols[i];
            writer.WriteBits(codes[symbol], codeLengthLengths[symbol]);

            int extra = CodeLengthExtra[symbol];
            if (extra != 0)
                writer.WriteBits(runExtras[i], extra);
        }
    }

    /// <summary>
    /// Run-length encodes a code-length sequence into the 19-symbol alphabet: 0…15 verbatim,
    /// 16 to repeat the previous length, 17 and 18 for runs of zeros.
    /// </summary>
    /// <returns>How many symbols were written.</returns>
    private static int PackCodeLengths(ReadOnlySpan<byte> lengths, Span<byte> symbols, Span<byte> extras)
    {
        int count = 0;
        int index = 0;

        while (index < lengths.Length)
        {
            byte value = lengths[index];
            int run = 1;

            while (index + run < lengths.Length && lengths[index + run] == value)
                run++;

            if (value != 0)
            {
                // A repeat code copies the previous length, so the first one goes out verbatim.
                symbols[count] = value;
                extras[count++] = 0;
                index++;
                run--;
            }

            // Runs of eleven or more zeros have their own, wider code.
            if (value == 0)
            {
                while (run >= 11)
                {
                    int take = Math.Min(run, 138);
                    symbols[count] = RepeatZeroLong;
                    extras[count++] = (byte)(take - 11);
                    index += take;
                    run -= take;
                }
            }

            const int shortestRun = 3;
            int longestRun = value == 0 ? 10 : 6;
            int repeatSymbol = value == 0 ? RepeatZeroShort : RepeatPrevious;

            while (run >= shortestRun)
            {
                int take = Math.Min(run, longestRun);
                symbols[count] = (byte)repeatSymbol;
                extras[count++] = (byte)(take - shortestRun);
                index += take;
                run -= take;
            }

            while (run > 0)
            {
                symbols[count] = value;
                extras[count++] = 0;
                index++;
                run--;
            }
        }

        return count;
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

    /// <summary>Emits one stored block: the header, the byte-aligned lengths, then the payload verbatim.</summary>
    private static void WriteStoredBlock(ref BitWriter writer, scoped ReadOnlySpan<byte> block, bool final)
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
    private static void ReadStoredBlock(ref BitReader reader, scoped Span<byte> destination, ref int written)
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

    /// <summary>Reads a dynamic block's code descriptions, then the block itself.</summary>
    private static void ReadDynamicBlock(
        ref BitReader reader, scoped Span<byte> destination, ref int written,
        scoped Span<uint> literalTable, scoped Span<uint> distanceTable)
    {
        int hlit = (int)reader.ReadBits(5) + FirstLengthSymbol;
        int hdist = (int)reader.ReadBits(5) + 1;
        int hclen = (int)reader.ReadBits(4) + MinCodeLengthCodes;

        if (reader.IsOverrun)
            throw Truncated();
        if (hlit > MaxLiteralCodes)
            throw new InvalidDataException($"Deflate block declares {hlit} literal codes; the most is {MaxLiteralCodes}.");
        if (hdist > DistanceAlphabetSize)
            throw new InvalidDataException($"Deflate block declares {hdist} distance codes; the most is {DistanceAlphabetSize}.");

        Span<byte> codeLengthLengths = stackalloc byte[CodeLengthAlphabetSize];
        codeLengthLengths.Clear();

        for (int i = 0; i < hclen; i++)
            codeLengthLengths[CodeLengthOrder[i]] = (byte)reader.ReadBits(3);

        if (reader.IsOverrun)
            throw Truncated();
        if (!CanonicalHuffman.IsDecodable(codeLengthLengths, CodeLengthBits))
            throw new InvalidDataException("Deflate code-length code is not a valid prefix code.");

        // Root bits equal to the longest code, so this table has no subtables.
        Span<uint> codeLengthTable = stackalloc uint[CanonicalHuffman.DecodeTableSize(
            CodeLengthAlphabetSize, CodeLengthBits, CodeLengthBits)];
        CanonicalHuffman.BuildDecodeTable(codeLengthLengths, codeLengthTable, CodeLengthBits, CodeLengthBits);

        Span<byte> sequence = stackalloc byte[MaxLiteralCodes + DistanceAlphabetSize];
        sequence.Clear();
        ReadCodeLengths(ref reader, codeLengthTable, sequence[..(hlit + hdist)]);

        Span<byte> literalLengths = stackalloc byte[LiteralAlphabetSize];
        Span<byte> distanceLengths = stackalloc byte[DistanceAlphabetSize];
        literalLengths.Clear();
        distanceLengths.Clear();
        sequence[..hlit].CopyTo(literalLengths);
        sequence.Slice(hlit, hdist).CopyTo(distanceLengths);

        if (!CanonicalHuffman.IsDecodable(literalLengths, MaxCodeLength))
            throw new InvalidDataException("Deflate literal/length code is not a valid prefix code.");
        if (!CanonicalHuffman.IsDecodable(distanceLengths, MaxCodeLength))
            throw new InvalidDataException("Deflate distance code is not a valid prefix code.");

        CanonicalHuffman.BuildDecodeTable(literalLengths, literalTable, MaxCodeLength, DynamicRootBits);
        CanonicalHuffman.BuildDecodeTable(distanceLengths, distanceTable, MaxCodeLength, DynamicRootBits);

        ReadHuffmanBlock(
            ref reader, destination, ref written,
            literalTable, DynamicRootBits, distanceTable, DynamicRootBits);
    }

    /// <summary>
    /// Reads the code lengths of the literal/length and distance alphabets, expanding the
    /// three repeat codes. The sequence covers both alphabets, so a repeat may cross from
    /// one into the other.
    /// </summary>
    private static void ReadCodeLengths(
        ref BitReader reader, scoped ReadOnlySpan<uint> table, scoped Span<byte> lengths)
    {
        int index = 0;

        while (index < lengths.Length)
        {
            int symbol = DecodeSymbol(ref reader, table, CodeLengthBits);

            if (reader.IsOverrun)
                throw Truncated();

            if (symbol < RepeatPrevious)
            {
                lengths[index++] = (byte)symbol;
                continue;
            }

            byte value = 0;
            int repeat;

            switch (symbol)
            {
                case RepeatPrevious:
                    if (index == 0)
                        throw new InvalidDataException("Deflate code-length repeat has nothing to repeat.");

                    value = lengths[index - 1];
                    repeat = 3 + (int)reader.ReadBits(2);
                    break;

                case RepeatZeroShort:
                    repeat = 3 + (int)reader.ReadBits(3);
                    break;

                default:
                    repeat = 11 + (int)reader.ReadBits(7);
                    break;
            }

            if (reader.IsOverrun)
                throw Truncated();
            if (index + repeat > lengths.Length)
                throw new InvalidDataException("Deflate code-length repeat runs past the end of the alphabets.");

            lengths.Slice(index, repeat).Fill(value);
            index += repeat;
        }
    }

    /// <summary>
    /// Reads one Huffman-coded block — fixed or dynamic, they differ only in the tables —
    /// appending it at <paramref name="written"/>.
    /// </summary>
    private static void ReadHuffmanBlock(
        ref BitReader reader, scoped Span<byte> destination, ref int written,
        scoped ReadOnlySpan<uint> literalTable, int literalRootBits,
        scoped ReadOnlySpan<uint> distanceTable, int distanceRootBits)
    {
        while (true)
        {
            int symbol = DecodeSymbol(ref reader, literalTable, literalRootBits);

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
            int distanceSymbol = DecodeSymbol(ref reader, distanceTable, distanceRootBits);

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
    private static int DecodeSymbol(ref BitReader reader, scoped ReadOnlySpan<uint> table, int rootBits)
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

    /// <summary>Total bits the given frequencies cost under the given code lengths.</summary>
    private static long SymbolBits(ReadOnlySpan<int> frequencies, ReadOnlySpan<byte> lengths)
    {
        long bits = 0;

        for (int symbol = 0; symbol < frequencies.Length; symbol++)
            bits += (long)frequencies[symbol] * lengths[symbol];

        return bits;
    }

    /// <summary>
    /// Raises frequencies until at least two symbols are used, so that every alphabet comes
    /// out as a complete prefix code with codes of at least one bit.
    /// </summary>
    private static void EnsureTwoSymbols(Span<int> frequencies)
    {
        int used = 0;

        foreach (int frequency in frequencies)
        {
            if (frequency > 0)
                used++;
        }

        for (int symbol = 0; used < 2 && symbol < frequencies.Length; symbol++)
        {
            if (frequencies[symbol] == 0)
            {
                frequencies[symbol] = 1;
                used++;
            }
        }
    }

    /// <summary>How many code lengths have to be transmitted: everything up to the last used one.</summary>
    private static int TransmittedCount(ReadOnlySpan<byte> lengths, int minimum, int maximum)
    {
        int count = maximum;

        while (count > minimum && lengths[count - 1] == 0)
            count--;

        return count;
    }

    /// <summary>The same, for the code-length alphabet, which is transmitted in a permuted order.</summary>
    private static int TransmittedCountInOrder(ReadOnlySpan<byte> codeLengthLengths)
    {
        int count = CodeLengthAlphabetSize;

        while (count > MinCodeLengthCodes && codeLengthLengths[CodeLengthOrder[count - 1]] == 0)
            count--;

        return count;
    }

    /// <summary>Exact size of the stored form, which is also the worst case for the whole codec.</summary>
    private static int StoredLength(int sourceLength)
    {
        long blocks = Math.Max(1, (sourceLength + MaxStoredBlock - 1) / MaxStoredBlock);
        long bound = sourceLength + blocks * StoredBlockOverhead;

        return bound > int.MaxValue ? int.MaxValue : (int)bound;
    }

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
