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
/// <c>LEN</c> and <c>NLEN</c> as little-endian 16-bit values — <c>NLEN</c> is the
/// one's complement of <c>LEN</c>, which catches a desynchronised reader immediately —
/// and then <c>LEN</c> raw bytes. A block holds at most 65 535 bytes, so longer input
/// becomes several blocks.
/// </para>
/// <para>
/// Compatibility is deliberate and exact: the output of <see cref="Encode"/> is a raw
/// deflate stream that any RFC 1951 decoder reads, and <see cref="Decode"/> reads what
/// they produce. That is what makes a reference implementation usable as a correctness
/// oracle in the tests — see ADR-0013. The stream carries no zlib or gzip wrapper: the
/// container supplies the length and the checksum.
/// </para>
/// <para>
/// Worst case: stored blocks cost 5 bytes of framing per 65 535 bytes of payload, under
/// 0.008 %. Empty input is one empty stored block, 5 bytes, rather than nothing, so that
/// the result is always a valid stream on its own.
/// </para>
/// <para>Complexity: stored blocks are O(n) time and O(1) additional space.</para>
/// <para>
/// Not implemented yet: <see cref="Encode"/> only emits stored blocks, and
/// <see cref="Decode"/> raises <see cref="NotSupportedException"/> for the two Huffman
/// block types. Fixed and dynamic blocks follow.
/// </para>
/// </remarks>
public sealed class DeflateCodec : ICodec
{
    /// <summary>Largest payload a single stored block can carry.</summary>
    private const int MaxStoredBlock = 0xFFFF;

    /// <summary>Framing cost of one stored block: the header byte plus LEN and NLEN.</summary>
    private const int StoredBlockOverhead = 5;

    private const uint StoredBlock = 0;
    private const uint FixedBlock = 1;
    private const uint DynamicBlock = 2;

    /// <inheritdoc/>
    public CodecId Id => CodecId.Deflate;

    /// <inheritdoc/>
    public string Name => "Deflate";

    /// <inheritdoc/>
    /// <remarks>
    /// Sized for the stored fallback, which is what an incompressible block ends up as.
    /// A Huffman block that came out larger than its input is never emitted, so this
    /// stays an upper bound once those are in.
    /// </remarks>
    public int MaxCompressedSize(int sourceLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceLength);

        long blocks = Math.Max(1, (sourceLength + MaxStoredBlock - 1) / MaxStoredBlock);
        long bound = sourceLength + blocks * StoredBlockOverhead;

        return bound > int.MaxValue ? int.MaxValue : (int)bound;
    }

    /// <inheritdoc/>
    public int Encode(ReadOnlySpan<byte> source, Span<byte> destination)
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

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">
    /// The stream contains a fixed or dynamic Huffman block, which is not implemented yet.
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

            written += type switch
            {
                StoredBlock => ReadStoredBlock(ref reader, destination[written..]),
                FixedBlock or DynamicBlock =>
                    throw new NotSupportedException(
                        $"Deflate block type {type} is not implemented yet; only stored blocks are."),
                _ => throw new InvalidDataException("Deflate block type 3 is reserved."),
            };
        }
        while (!final);

        return written;
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

    /// <summary>Reads one stored block into <paramref name="destination"/> and returns its length.</summary>
    private static int ReadStoredBlock(ref BitReader reader, Span<byte> destination)
    {
        reader.AlignToByte();

        int length = (int)reader.ReadBits(16);
        int complement = (int)reader.ReadBits(16);

        if (reader.IsOverrun)
            throw Truncated();
        if ((~length & MaxStoredBlock) != complement)
            throw new InvalidDataException("Deflate stored block length does not match its complement.");
        if (length > destination.Length)
            throw new InvalidDataException("Deflate stream expands past the declared block size.");

        for (int i = 0; i < length; i++)
            destination[i] = (byte)reader.ReadBits(8);

        if (reader.IsOverrun)
            throw Truncated();

        return length;
    }

    private static InvalidDataException Truncated() =>
        new("Truncated Deflate stream.");
}
