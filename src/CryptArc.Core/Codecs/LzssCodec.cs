using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using CryptArc.Core.Coding;

namespace CryptArc.Core.Codecs;

/// <summary>
/// LZSS: a Lempel–Ziv sliding-window compressor that replaces a repeated byte
/// sequence with a back-reference to its previous occurrence.
/// </summary>
/// <remarks>
/// <para>Stream format. Every group starts with one control byte:</para>
/// <list type="bullet">
///   <item><description>
///     <c>0xxxxxxx</c>: literal run. The next <c>x + 1</c> bytes (1…128) are copied verbatim.
///   </description></item>
///   <item><description>
///     <c>1xxxxxxx dd dd</c>: match. Copy <c>x + 4</c> bytes (4…131) from <c>distance</c> bytes
///     back in the output. <c>dd dd</c> is <c>distance - 1</c> as a little-endian
///     <see cref="ushort"/>, so the distance is 1…32768 (a 32 KiB window). The distance can be
///     smaller than the length. The copy then overlaps its own output, which is how a run of
///     one byte becomes a single match with distance 1.
///   </description></item>
/// </list>
/// <para>
/// There is no header and no end marker: the stream ends where the input ends, and the
/// container's block size tells the decoder how many bytes to expect.
/// </para>
/// <para>
/// A match costs 3 bytes, so the shortest match worth emitting is 4 bytes. Literals are
/// grouped into runs like in <see cref="RleCodec"/>, which bounds the worst case. Incompressible
/// input grows by one control byte per 128 bytes (&lt; 0.8 %). The textbook form, with one flag
/// bit per token, grows it by 12.5 %.
/// </para>
/// <para>
/// Match search. <see cref="Encode"/> keeps a hash table of chained positions: <c>head[h]</c> is
/// the newest position whose next 3 bytes hash to <c>h</c>, and <c>prev[p mod 32768]</c> links to
/// the previous one. It walks at most <see cref="MaxChainLength"/> links per position. With
/// <see cref="LazyMatching"/> on, it also looks one byte ahead and emits a literal first when
/// the next position starts a longer match, the same heuristic zlib uses. Match length is
/// measured 8 bytes at a time: XOR two <see cref="ulong"/> words, and
/// <see cref="BitOperations.TrailingZeroCount(ulong)"/> of the result gives the first
/// differing byte.
/// </para>
/// <para>
/// <see cref="EncodeNaive"/> is the baseline: for every position it scans the whole window,
/// comparing one byte at a time, and takes the longest match greedily. Both encoders break
/// ties toward the nearest match. With an unbounded chain and lazy matching off,
/// <see cref="Encode"/> therefore produces the same bytes as <see cref="EncodeNaive"/>, and the
/// tests check this.
/// </para>
/// <para>
/// Complexity. Naive encode: O(n·W) time with W = 32768. Hash-chain encode: O(n·k) time with
/// k = <see cref="MaxChainLength"/>, plus 256 KiB of pooled scratch for the two tables.
/// Decode: O(n) time, O(1) additional space.
/// </para>
/// </remarks>
public sealed class LzssCodec : ICodec
{
    /// <summary>Size of the sliding window, and so the largest distance a match can reach.</summary>
    public const int WindowSize = 1 << 15;

    /// <summary>The shortest match the format can express.</summary>
    public const int MinMatch = 4;

    /// <summary>The longest match the format can express.</summary>
    public const int MaxMatch = MinMatch + 127;

    /// <summary>The default for <see cref="MaxChainLength"/>, used by the registered instance.</summary>
    public const int DefaultMaxChainLength = 128;

    private const int MaxLiteralRun = 128;
    private const int MatchFlag = 0x80;
    private const int MatchTokenSize = 3;

    /// <summary>
    /// Initializes a new instance of the <see cref="LzssCodec"/> class.
    /// </summary>
    /// <param name="maxChainLength">
    /// The largest number of earlier positions the encoder compares for one match. Larger values
    /// find longer matches more slowly. Use <see cref="int.MaxValue"/> to search the whole window.
    /// </param>
    /// <param name="lazyMatching">
    /// <see langword="true"/> to look one byte ahead before emitting a match; otherwise,
    /// <see langword="false"/> to take every match greedily.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxChainLength"/> is less than 1.</exception>
    public LzssCodec(int maxChainLength = DefaultMaxChainLength, bool lazyMatching = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChainLength, 1);

        MaxChainLength = maxChainLength;
        LazyMatching = lazyMatching;
    }

    /// <summary>Gets the largest number of earlier positions compared for one match.</summary>
    /// <value>The chain limit. The default is <see cref="DefaultMaxChainLength"/>.</value>
    public int MaxChainLength { get; }

    /// <summary>Gets a value that indicates whether the encoder looks one byte ahead before emitting a match.</summary>
    /// <value><see langword="true"/> if lazy matching is on; otherwise, <see langword="false"/>. The default is <see langword="true"/>.</value>
    public bool LazyMatching { get; }

    /// <inheritdoc/>
    public CodecId Id => CodecId.Lzss;

    /// <inheritdoc/>
    public string Name => "LZSS";

    /// <inheritdoc/>
    /// <remarks>Incompressible input costs one control byte per 128 literals.</remarks>
    public int MaxCompressedSize(int sourceLength) =>
        sourceLength + (sourceLength + MaxLiteralRun - 1) / MaxLiteralRun;

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
                return EncodeCore(source, destination, ref finder, LazyMatching);
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
    /// Kept only as the benchmark baseline and as a correctness oracle for <see cref="Encode"/>.
    /// </summary>
    /// <param name="source">The bytes to compress.</param>
    /// <param name="destination">The buffer that receives the compressed stream.</param>
    /// <returns>The number of bytes written, or -1 when <paramref name="destination"/> is too small.</returns>
    public static int EncodeNaive(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var finder = new LinearScanFinder(WindowSize, MinMatch, MaxMatch);
        return EncodeCore(source, destination, ref finder, lazy: false);
    }

    /// <inheritdoc/>
    public int Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int read = 0;
        int written = 0;

        while (read < source.Length)
        {
            byte control = source[read++];

            if ((control & MatchFlag) == 0)
            {
                int literalLength = control + 1;

                if (literalLength > source.Length - read)
                    throw new InvalidDataException("Truncated literal run in LZSS stream.");
                if (literalLength > destination.Length - written)
                    throw ExpandsPastBlock();

                source.Slice(read, literalLength).CopyTo(destination[written..]);
                read += literalLength;
                written += literalLength;
                continue;
            }

            int length = (control & ~MatchFlag) + MinMatch;

            if (source.Length - read < MatchTokenSize - 1)
                throw new InvalidDataException("Truncated match in LZSS stream.");

            int distance = BinaryPrimitives.ReadUInt16LittleEndian(source[read..]) + 1;
            read += MatchTokenSize - 1;

            if (distance > WindowSize)
                throw new InvalidDataException("LZSS match distance is larger than the window.");
            if (distance > written)
                throw new InvalidDataException("LZSS match points before the start of the block.");
            if (length > destination.Length - written)
                throw ExpandsPastBlock();

            CopyMatch(destination, written, distance, length);
            written += length;
        }

        return written;
    }

    /// <summary>
    /// Counts how many leading bytes <paramref name="left"/> and <paramref name="right"/> share,
    /// comparing 8 bytes at a time.
    /// </summary>
    /// <param name="left">The first sequence.</param>
    /// <param name="right">The second sequence.</param>
    /// <param name="limit">The largest count to report.</param>
    /// <returns>The length of the common prefix, at most <paramref name="limit"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="limit"/> is negative or larger than either sequence.
    /// </exception>
    public static int MatchLength(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, int limit)
    {
        CheckLimit(left, right, limit);
        return MatchLengths.Wide(left, right, limit);
    }

    /// <summary>
    /// Counts how many leading bytes <paramref name="left"/> and <paramref name="right"/> share,
    /// comparing one byte at a time. Kept only as the baseline for <see cref="MatchLength"/>.
    /// </summary>
    /// <param name="left">The first sequence.</param>
    /// <param name="right">The second sequence.</param>
    /// <param name="limit">The largest count to report.</param>
    /// <returns>The length of the common prefix, at most <paramref name="limit"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="limit"/> is negative or larger than either sequence.
    /// </exception>
    public static int MatchLengthNaive(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, int limit)
    {
        CheckLimit(left, right, limit);
        return MatchLengths.Bytewise(left, right, limit);
    }

    /// <summary>
    /// The parser shared by both encoders. Literals collect into runs. A match is emitted when
    /// <typeparamref name="TFinder"/> finds one, unless lazy matching finds a longer one at the
    /// next position.
    /// </summary>
    private static int EncodeCore<TFinder>(
        ReadOnlySpan<byte> source, Span<byte> destination, ref TFinder finder, bool lazy)
        where TFinder : struct, IMatchFinder
    {
        int written = 0;
        int position = 0;
        int literalStart = 0;

        Match match = finder.Find(source, position);

        while (position < source.Length)
        {
            if (match.Length < MinMatch)
            {
                if (!TryTakeLiteral(source, ref position, ref literalStart, destination, ref written))
                    return -1;

                match = finder.Find(source, position);
                continue;
            }

            if (lazy && match.Length < MaxMatch)
            {
                Match next = finder.Find(source, position + 1);
                if (next.Length > match.Length)
                {
                    // Emit the current byte as a literal and take the longer match next time round.
                    if (!TryTakeLiteral(source, ref position, ref literalStart, destination, ref written))
                        return -1;

                    match = next;
                    continue;
                }
            }

            if (!TryWriteLiterals(source, literalStart, position, destination, ref written)
                || !TryWriteMatch(match, destination, ref written))
            {
                return -1;
            }

            position += match.Length;
            literalStart = position;
            match = finder.Find(source, position);
        }

        return TryWriteLiterals(source, literalStart, position, destination, ref written) ? written : -1;
    }

    /// <summary>Adds the byte at <paramref name="position"/> to the pending literal run, flushing a full run.</summary>
    private static bool TryTakeLiteral(
        ReadOnlySpan<byte> source, ref int position, ref int literalStart, Span<byte> destination, ref int written)
    {
        position++;
        if (position - literalStart < MaxLiteralRun)
            return true;

        if (!TryWriteLiterals(source, literalStart, position, destination, ref written))
            return false;

        literalStart = position;
        return true;
    }

    private static bool TryWriteLiterals(
        ReadOnlySpan<byte> source, int start, int end, Span<byte> destination, ref int written)
    {
        int length = end - start;
        if (length == 0)
            return true;

        if (1 + length > destination.Length - written)
            return false;

        destination[written++] = (byte)(length - 1);
        source[start..end].CopyTo(destination[written..]);
        written += length;
        return true;
    }

    private static bool TryWriteMatch(Match match, Span<byte> destination, ref int written)
    {
        if (MatchTokenSize > destination.Length - written)
            return false;

        destination[written] = (byte)(MatchFlag | (match.Length - MinMatch));
        BinaryPrimitives.WriteUInt16LittleEndian(destination[(written + 1)..], (ushort)(match.Distance - 1));
        written += MatchTokenSize;
        return true;
    }

    private static void CopyMatch(Span<byte> output, int written, int distance, int length)
    {
        int from = written - distance;

        if (distance >= length)
        {
            output.Slice(from, length).CopyTo(output[written..]);
        }
        else if (distance == 1)
        {
            output.Slice(written, length).Fill(output[from]);
        }
        else
        {
            // The source overlaps the bytes being written, so copy forwards one byte at a time.
            for (int i = 0; i < length; i++)
                output[written + i] = output[from + i];
        }
    }

    private static void CheckLimit(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, Math.Min(left.Length, right.Length));
    }

    private static InvalidDataException ExpandsPastBlock() =>
        new("LZSS stream expands past the declared block size.");

}
