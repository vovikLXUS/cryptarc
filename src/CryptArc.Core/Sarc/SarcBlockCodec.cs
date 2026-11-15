using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using CryptArc.Core.Coding;

namespace CryptArc.Core.Sarc;

/// <summary>How the LZ77 tokens of a block are chosen.</summary>
public enum SarcParse
{
    /// <summary>Take the longest match at every position.</summary>
    Greedy,

    /// <summary>
    /// Emit a literal instead when the next position has a strictly longer match
    /// ("лінива жадібність" in the Project 2 brief).
    /// </summary>
    Lazy,
}

/// <summary>
/// The block coding of FORMAT_SPEC_P2 §3: LZ77 elements with three canonical Huffman trees.
/// Blocks are independent: a reference never reaches into a previous block.
/// </summary>
/// <remarks>
/// <para>Stream format of one coded block, all integers big-endian, bits MSB-first:</para>
/// <code>
/// LIT table     alphabet 0…256: bytes 0…255, 256 = "a reference follows"
/// LEN table     alphabet 0…255: match length − 3
/// DIST table    alphabet 0…15:  bit length of (distance − 1)
/// token_count   uint32
/// stream_len    uint32
/// stream        stream_len bytes, zero-padded to a byte
///
/// element:  literal   → LIT(byte)
///           reference → LIT(256) LEN(length − 3) DIST(code) [code − 1 low bits of distance − 1]
/// </code>
/// <para>
/// A table is <c>nsym</c> (uint16) and then <c>nsym</c> entries of symbol (uint16) and
/// length (uint8) in ascending symbol order. Codes are canonical (§3.4), at most 15 bits;
/// a lone symbol has length 1 and code <c>0</c>. Match parameters: minimum 3, maximum
/// 258, window 32768.
/// </para>
/// <para>
/// Matches are found with <see cref="HashChainFinder"/>; <see cref="LinearScanFinder"/>
/// is the naive baseline reachable through the internal overload (hard rule 4). Decoding
/// is O(n·15) bit reads in the worst case. The type is stateless and thread-safe.
/// </para>
/// </remarks>
public static class SarcBlockCodec
{
    /// <summary>Shortest match the format expresses.</summary>
    public const int MinMatch = 3;

    /// <summary>Longest match the format expresses.</summary>
    public const int MaxMatch = 258;

    /// <summary>Largest distance a reference may reach back.</summary>
    public const int Window = 32768;

    /// <summary>The LIT symbol that announces a reference.</summary>
    public const int ReferenceMarker = 256;

    /// <summary>Hash-chain depth of the registered match search.</summary>
    public const int DefaultMaxChain = 128;

    private const int LiteralAlphabet = 257;
    private const int LengthAlphabet = 256;
    private const int DistanceAlphabet = 16;
    private const int CountsSize = sizeof(uint) * 2;
    private const uint MatchFlag = 0x8000_0000;
    private const int LengthShift = 16;
    private const uint DistanceMask = 0xFFFF;

    /// <summary>Encodes one block.</summary>
    /// <param name="raw">The block's bytes.</param>
    /// <param name="parse">How tokens are chosen.</param>
    /// <returns>The coded block. It may be larger than <paramref name="raw"/>; the caller then stores the block raw.</returns>
    public static byte[] Encode(ReadOnlySpan<byte> raw, SarcParse parse = SarcParse.Lazy) =>
        Encode(raw, parse, linearScan: false);

    /// <summary>Encodes one block, optionally with the O(n·W) baseline match search.</summary>
    /// <param name="raw">The block's bytes.</param>
    /// <param name="parse">How tokens are chosen.</param>
    /// <param name="linearScan">Use <see cref="LinearScanFinder"/> instead of hash chains.</param>
    /// <param name="maxChain">Hash-chain depth.</param>
    /// <returns>The coded block.</returns>
    internal static byte[] Encode(ReadOnlySpan<byte> raw, SarcParse parse, bool linearScan, int maxChain = DefaultMaxChain)
    {
        uint[] tokens = ArrayPool<uint>.Shared.Rent(Math.Max(1, raw.Length));
        try
        {
            Span<int> literalFrequencies = stackalloc int[LiteralAlphabet];
            Span<int> lengthFrequencies = stackalloc int[LengthAlphabet];
            Span<int> distanceFrequencies = stackalloc int[DistanceAlphabet];
            literalFrequencies.Clear();
            lengthFrequencies.Clear();
            distanceFrequencies.Clear();

            bool lazy = parse == SarcParse.Lazy;
            int count;
            long extraBits;

            if (linearScan)
            {
                var finder = new LinearScanFinder(Window, MinMatch, MaxMatch);
                count = Tokenize(raw, ref finder, lazy, tokens, literalFrequencies, lengthFrequencies, distanceFrequencies, out extraBits);
            }
            else
            {
                int[] head = ArrayPool<int>.Shared.Rent(HashChainFinder.HashSize);
                int[] prev = ArrayPool<int>.Shared.Rent(Window);
                try
                {
                    head.AsSpan(0, HashChainFinder.HashSize).Fill(HashChainFinder.NoPosition);
                    var finder = new HashChainFinder(head, prev, Window, MinMatch, MaxMatch, maxChain);
                    count = Tokenize(raw, ref finder, lazy, tokens, literalFrequencies, lengthFrequencies, distanceFrequencies, out extraBits);
                }
                finally
                {
                    ArrayPool<int>.Shared.Return(prev);
                    ArrayPool<int>.Shared.Return(head);
                }
            }

            Span<byte> literalLengths = stackalloc byte[LiteralAlphabet];
            Span<byte> lengthLengths = stackalloc byte[LengthAlphabet];
            Span<byte> distanceLengths = stackalloc byte[DistanceAlphabet];
            SarcCodeTable.BuildLengths(literalFrequencies, literalLengths);
            SarcCodeTable.BuildLengths(lengthFrequencies, lengthLengths);
            SarcCodeTable.BuildLengths(distanceFrequencies, distanceLengths);

            long bits = extraBits
                + WeightedLength(literalFrequencies, literalLengths)
                + WeightedLength(lengthFrequencies, lengthLengths)
                + WeightedLength(distanceFrequencies, distanceLengths);
            int streamLength = checked((int)((bits + 7) / 8));

            int tablesSize = SarcCodeTable.SerializedSize(literalLengths)
                + SarcCodeTable.SerializedSize(lengthLengths)
                + SarcCodeTable.SerializedSize(distanceLengths);

            byte[] coded = new byte[checked(tablesSize + CountsSize + streamLength)];
            int position = SarcCodeTable.Write(literalLengths, coded);
            position += SarcCodeTable.Write(lengthLengths, coded.AsSpan(position));
            position += SarcCodeTable.Write(distanceLengths, coded.AsSpan(position));
            BinaryPrimitives.WriteUInt32BigEndian(coded.AsSpan(position), (uint)count);
            BinaryPrimitives.WriteUInt32BigEndian(coded.AsSpan(position + sizeof(uint)), (uint)streamLength);
            position += CountsSize;

            Span<uint> literalCodes = stackalloc uint[LiteralAlphabet];
            Span<uint> lengthCodes = stackalloc uint[LengthAlphabet];
            Span<uint> distanceCodes = stackalloc uint[DistanceAlphabet];
            SarcCodeTable.AssignCodes(literalLengths, literalCodes);
            SarcCodeTable.AssignCodes(lengthLengths, lengthCodes);
            SarcCodeTable.AssignCodes(distanceLengths, distanceCodes);

            var writer = new MsbBitWriter(coded.AsSpan(position));
            for (int i = 0; i < count; i++)
            {
                uint token = tokens[i];
                if ((token & MatchFlag) == 0)
                {
                    writer.Write(literalCodes[(int)token], literalLengths[(int)token]);
                    continue;
                }

                int lengthSymbol = (int)(token >> LengthShift & 0xFF);
                uint d = token & DistanceMask;
                int distanceSymbol = BitLength(d);
                int extra = Math.Max(0, distanceSymbol - 1);

                writer.Write(literalCodes[ReferenceMarker], literalLengths[ReferenceMarker]);
                writer.Write(lengthCodes[lengthSymbol], lengthLengths[lengthSymbol]);
                writer.Write(distanceCodes[distanceSymbol], distanceLengths[distanceSymbol]);
                writer.Write(d, extra);
            }

            if (writer.Flush() != streamLength)
                throw new InvalidOperationException("The bit count of the stream was predicted wrongly.");

            return coded;
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(tokens);
        }
    }

    /// <summary>Decodes one block.</summary>
    /// <param name="coded">Exactly <c>coded_len</c> bytes of the block.</param>
    /// <param name="destination">Receives the block; its length is <c>raw_len</c>.</param>
    /// <exception cref="SarcException">
    /// <see cref="SarcErrorCode.Code"/> for an invalid table or code, a stream that ends
    /// inside an element, a reference outside the block, or non-zero padding;
    /// <see cref="SarcErrorCode.Payload"/> when the section lengths disagree with each
    /// other or with <c>raw_len</c>, or when bytes follow the stream.
    /// </exception>
    public static void Decode(ReadOnlySpan<byte> coded, Span<byte> destination)
    {
        Span<byte> literalLengths = stackalloc byte[LiteralAlphabet];
        Span<byte> lengthLengths = stackalloc byte[LengthAlphabet];
        Span<byte> distanceLengths = stackalloc byte[DistanceAlphabet];

        int position = SarcCodeTable.Read(coded, literalLengths, "LIT");
        position += SarcCodeTable.Read(coded[position..], lengthLengths, "LEN");
        position += SarcCodeTable.Read(coded[position..], distanceLengths, "DIST");

        if (coded.Length - position < CountsSize)
            throw new SarcException(SarcErrorCode.Payload, "The block ends before token_count and stream_len.");

        uint tokenCount = BinaryPrimitives.ReadUInt32BigEndian(coded[position..]);
        uint streamLength = BinaryPrimitives.ReadUInt32BigEndian(coded[(position + sizeof(uint))..]);
        position += CountsSize;

        int remaining = coded.Length - position;
        if (streamLength > remaining)
            throw new SarcException(SarcErrorCode.Payload, $"stream_len {streamLength} runs past the end of the block.");

        if (streamLength < remaining)
            throw new SarcException(SarcErrorCode.Payload, $"{remaining - streamLength} bytes follow the stream inside the block.");

        if (tokenCount > destination.Length)
            throw new SarcException(SarcErrorCode.Payload, $"token_count {tokenCount} is larger than raw_len {destination.Length}.");

        if (destination.Length > (long)tokenCount * MaxMatch)
            throw new SarcException(SarcErrorCode.Payload, $"{tokenCount} elements cannot produce raw_len {destination.Length}; each yields at most {MaxMatch} bytes.");

        Span<int> literalCounts = stackalloc int[SarcCodeTable.MaxCodeLength + 1];
        Span<int> lengthCounts = stackalloc int[SarcCodeTable.MaxCodeLength + 1];
        Span<int> distanceCounts = stackalloc int[SarcCodeTable.MaxCodeLength + 1];
        Span<ushort> literalSymbols = stackalloc ushort[LiteralAlphabet];
        Span<ushort> lengthSymbols = stackalloc ushort[LengthAlphabet];
        Span<ushort> distanceSymbols = stackalloc ushort[DistanceAlphabet];
        var literals = new SarcSymbolDecoder(literalLengths, literalCounts, literalSymbols);
        var lengths = new SarcSymbolDecoder(lengthLengths, lengthCounts, lengthSymbols);
        var distances = new SarcSymbolDecoder(distanceLengths, distanceCounts, distanceSymbols);

        // Scoped like the decoders' stackalloc tables, so they may be passed together.
        scoped var reader = new MsbBitReader(coded[position..]);
        int written = 0;

        for (uint element = 0; element < tokenCount; element++)
        {
            int symbol = Expect(literals.Decode(ref reader), "LIT", element);
            if (symbol != ReferenceMarker)
            {
                if (written == destination.Length)
                    throw new SarcException(SarcErrorCode.Payload, $"The block decodes to more than raw_len {destination.Length} bytes.");

                destination[written++] = (byte)symbol;
                continue;
            }

            int length = Expect(lengths.Decode(ref reader), "LEN", element) + MinMatch;
            int distanceSymbol = Expect(distances.Decode(ref reader), "DIST", element);
            int extra = Math.Max(0, distanceSymbol - 1);
            if (!reader.TryRead(extra, out uint extraBits))
                throw new SarcException(SarcErrorCode.Code, $"The stream ends inside the distance bits of element {element}.");

            int distance = distanceSymbol switch
            {
                0 => 1,
                1 => 2,
                _ => (1 << (distanceSymbol - 1) | (int)extraBits) + 1,
            };

            if (distance > written)
                throw new SarcException(SarcErrorCode.Code, $"Element {element} refers {distance} bytes back with only {written} decoded.");

            if (length > destination.Length - written)
                throw new SarcException(SarcErrorCode.Code, $"Element {element} copies past the end of the {destination.Length}-byte block.");

            LzOutput.CopyMatch(destination, written, distance, length);
            written += length;
        }

        if (written != destination.Length)
            throw new SarcException(SarcErrorCode.Payload, $"The block decodes to {written} bytes; raw_len is {destination.Length}.");

        if (!reader.PaddingIsZero)
            throw new SarcException(SarcErrorCode.Code, "The padding after the last element is not zero.");

        if (reader.BytesConsumed != streamLength)
            throw new SarcException(SarcErrorCode.Payload, $"{streamLength - reader.BytesConsumed} bytes of the stream follow its last element.");
    }

    /// <summary>The number of bits needed to write <paramref name="value"/>; 0 for 0.</summary>
    private static int BitLength(uint value) => 32 - BitOperations.LeadingZeroCount(value);

    private static int Expect(int symbol, string tree, uint element) => symbol switch
    {
        SarcSymbolDecoder.InvalidCode => throw new SarcException(SarcErrorCode.Code, $"Element {element} is not a valid {tree} code."),
        SarcSymbolDecoder.EndOfStream => throw new SarcException(SarcErrorCode.Code, $"The stream ends inside element {element}."),
        _ => symbol,
    };

    private static long WeightedLength(ReadOnlySpan<int> frequencies, ReadOnlySpan<byte> lengths)
    {
        long total = 0;
        for (int symbol = 0; symbol < frequencies.Length; symbol++)
            total += (long)frequencies[symbol] * lengths[symbol];

        return total;
    }

    /// <summary>
    /// Turns the block into tokens and counts each alphabet. Same parser as Deflate's: a
    /// match is taken when found, unless lazy matching finds a longer one one byte later.
    /// </summary>
    private static int Tokenize<TFinder>(
        ReadOnlySpan<byte> source,
        ref TFinder finder,
        bool lazy,
        Span<uint> tokens,
        Span<int> literalFrequencies,
        Span<int> lengthFrequencies,
        Span<int> distanceFrequencies,
        out long extraBits)
        where TFinder : struct, IMatchFinder
    {
        extraBits = 0;
        int count = 0;
        int position = 0;
        Match match = source.IsEmpty ? Match.None : finder.Find(source, position);

        while (position < source.Length)
        {
            if (match.Length < MinMatch)
            {
                byte literal = source[position];
                tokens[count++] = literal;
                literalFrequencies[literal]++;
                position++;
                match = position < source.Length ? finder.Find(source, position) : Match.None;
                continue;
            }

            if (lazy && match.Length < MaxMatch)
            {
                Match next = finder.Find(source, position + 1);
                if (next.Length > match.Length)
                {
                    byte literal = source[position];
                    tokens[count++] = literal;
                    literalFrequencies[literal]++;
                    position++;
                    match = next;
                    continue;
                }
            }

            uint d = (uint)(match.Distance - 1);
            int lengthSymbol = match.Length - MinMatch;
            int distanceSymbol = BitLength(d);

            tokens[count++] = MatchFlag | (uint)lengthSymbol << LengthShift | d;
            literalFrequencies[ReferenceMarker]++;
            lengthFrequencies[lengthSymbol]++;
            distanceFrequencies[distanceSymbol]++;
            extraBits += Math.Max(0, distanceSymbol - 1);

            position += match.Length;
            match = position < source.Length ? finder.Find(source, position) : Match.None;
        }

        return count;
    }
}
