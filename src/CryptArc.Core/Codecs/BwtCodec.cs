using System.Buffers;
using System.Buffers.Binary;
using CryptArc.Core.Transforms;

namespace CryptArc.Core.Codecs;

/// <summary>
/// Block-sorting compression in the bzip2 style: Burrows–Wheeler transform, then
/// move-to-front, then zero-run encoding, then canonical Huffman coding.
/// </summary>
/// <remarks>
/// <para>
/// The first three stages compress nothing on their own. The transform clusters bytes
/// that share a context, move-to-front turns those clusters into runs of small numbers,
/// and zero-run encoding collapses the zeros; only then does Huffman find a skewed
/// distribution worth coding. Each stage lives in its own class under
/// <c>CryptArc.Core.Transforms</c>.
/// </para>
/// <para>Stream format — an empty input encodes to zero bytes, anything else starts with a mode byte:</para>
/// <list type="bullet">
///   <item><description>
///     <c>0</c> — transformed. Bytes 1…4: the BWT primary index, little-endian.
///     Bytes 5…: a <see cref="HuffmanCodec"/> stream of the zero-run encoded
///     move-to-front output.
///   </description></item>
///   <item><description>
///     <c>1</c> — stored. Bytes 1…: the input, verbatim.
///   </description></item>
/// </list>
/// <para>
/// The stored mode is what bounds the worst case: zero-run encoding may expand
/// incompressible input, so whenever the transformed stream would be longer than the
/// input plus one byte, the block is stored instead and grows by exactly one byte.
/// </para>
/// <para>
/// Complexity: O(n log n) time for encoding, dominated by the suffix sort; O(n) for
/// decoding. Both use O(n) pooled scratch memory: one int array and two byte buffers.
/// </para>
/// </remarks>
public sealed class BwtCodec : ICodec
{
    private const byte ModeTransformed = 0;
    private const byte ModeStored = 1;
    private const int TransformedHeaderSize = 1 + sizeof(uint);

    private static readonly HuffmanCodec Entropy = new();

    public CodecId Id => CodecId.Bwt;

    public string Name => "BWT";

    public int MaxCompressedSize(int sourceLength) =>
        sourceLength == 0 ? 0 : sourceLength + 1;

    public int Encode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int n = source.Length;
        if (n == 0)
            return 0;

        int limit = Math.Min(destination.Length, MaxCompressedSize(n));

        if (limit > TransformedHeaderSize)
        {
            byte[] columnBuffer = ArrayPool<byte>.Shared.Rent(n);
            byte[] runBuffer = ArrayPool<byte>.Shared.Rent(ZeroRunLength.MaxEncodedSize(n));

            try
            {
                Span<byte> column = columnBuffer.AsSpan(0, n);
                int primaryIndex = BurrowsWheeler.Forward(source, column);
                MoveToFront.Encode(column);

                int runLength = ZeroRunLength.Encode(column, runBuffer);
                int packed = Entropy.Encode(runBuffer.AsSpan(0, runLength), destination[TransformedHeaderSize..limit]);

                if (packed >= 0)
                {
                    destination[0] = ModeTransformed;
                    BinaryPrimitives.WriteUInt32LittleEndian(destination[1..], (uint)primaryIndex);
                    return TransformedHeaderSize + packed;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(columnBuffer);
                ArrayPool<byte>.Shared.Return(runBuffer);
            }
        }

        if (destination.Length < n + 1)
            return -1;

        destination[0] = ModeStored;
        source.CopyTo(destination[1..]);
        return n + 1;
    }

    public int Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty)
            return 0;

        byte mode = source[0];

        if (mode == ModeStored)
        {
            ReadOnlySpan<byte> payload = source[1..];
            if (payload.Length > destination.Length)
                throw new InvalidDataException("Stored BWT block is larger than the declared block size.");

            payload.CopyTo(destination);
            return payload.Length;
        }

        if (mode != ModeTransformed)
            throw new InvalidDataException($"Unknown BWT block mode {mode}.");
        if (source.Length < TransformedHeaderSize)
            throw new InvalidDataException("Truncated BWT block header.");

        uint primaryIndex = BinaryPrimitives.ReadUInt32LittleEndian(source[1..]);
        if (primaryIndex > (uint)destination.Length)
            throw new InvalidDataException("BWT primary index is out of range.");

        int capacity = destination.Length;
        byte[] runBuffer = ArrayPool<byte>.Shared.Rent(ZeroRunLength.MaxEncodedSize(capacity));
        byte[] columnBuffer = ArrayPool<byte>.Shared.Rent(capacity);

        try
        {
            int runLength = Entropy.Decode(source[TransformedHeaderSize..], runBuffer.AsSpan(0, ZeroRunLength.MaxEncodedSize(capacity)));
            int n = ZeroRunLength.Decode(runBuffer.AsSpan(0, runLength), columnBuffer.AsSpan(0, capacity));

            Span<byte> column = columnBuffer.AsSpan(0, n);
            MoveToFront.Decode(column);
            BurrowsWheeler.Inverse(column, (int)primaryIndex, destination);
            return n;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(runBuffer);
            ArrayPool<byte>.Shared.Return(columnBuffer);
        }
    }
}
