namespace CryptArc.Core.Codecs;

/// <summary>
/// Byte oriented run-length encoding in the PackBits style.
/// </summary>
/// <remarks>
/// <para>Stream format — every group starts with one control byte:</para>
/// <list type="bullet">
///   <item><description>
///     <c>0xxxxxxx</c> — literal run: the next <c>x + 1</c> bytes (1…128) are copied verbatim.
///   </description></item>
///   <item><description>
///     <c>1xxxxxxx</c> — repeat run: the single byte that follows is repeated <c>x + 3</c> times (3…130).
///   </description></item>
/// </list>
/// <para>
/// Encoding a literal run rather than one control byte per byte is what keeps the
/// worst case bounded: incompressible input grows by only 1 byte per 128
/// (&lt; 0.8 %), whereas the textbook "count + value" pair form doubles it.
/// </para>
/// <para>Complexity: O(n) time, O(1) additional space, single pass.</para>
/// </remarks>
public sealed class RleCodec : ICodec
{
    private const int MaxLiteralRun = 128;
    private const int MinRepeatRun = 3;
    private const int MaxRepeatRun = MinRepeatRun + 127; // 130

    public CodecId Id => CodecId.Rle;

    public string Name => "RLE";

    public int MaxCompressedSize(int sourceLength) =>
        sourceLength + (sourceLength + MaxLiteralRun - 1) / MaxLiteralRun;

    public int Encode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int read = 0;
        int written = 0;

        while (read < source.Length)
        {
            int runLength = MeasureRun(source, read);

            if (runLength >= MinRepeatRun)
            {
                if (written + 2 > destination.Length)
                    return -1;

                destination[written++] = (byte)(0x80 | (runLength - MinRepeatRun));
                destination[written++] = source[read];
                read += runLength;
                continue;
            }

            int literalStart = read;
            int literalLength = 0;

            // Collect literals until a worthwhile run appears or the group is full.
            while (read < source.Length && literalLength < MaxLiteralRun && !StartsRun(source, read))
            {
                read++;
                literalLength++;
            }

            if (written + 1 + literalLength > destination.Length)
                return -1;

            destination[written++] = (byte)(literalLength - 1);
            source.Slice(literalStart, literalLength).CopyTo(destination[written..]);
            written += literalLength;
        }

        return written;
    }

    public int Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int read = 0;
        int written = 0;

        while (read < source.Length)
        {
            byte control = source[read++];

            if ((control & 0x80) == 0)
            {
                int literalLength = control + 1;

                if (read + literalLength > source.Length)
                    throw new InvalidDataException("Truncated literal run in RLE stream.");
                if (written + literalLength > destination.Length)
                    throw new InvalidDataException("RLE stream expands past the declared block size.");

                source.Slice(read, literalLength).CopyTo(destination[written..]);
                read += literalLength;
                written += literalLength;
            }
            else
            {
                int repeatLength = (control & 0x7F) + MinRepeatRun;

                if (read >= source.Length)
                    throw new InvalidDataException("Truncated repeat run in RLE stream.");
                if (written + repeatLength > destination.Length)
                    throw new InvalidDataException("RLE stream expands past the declared block size.");

                destination.Slice(written, repeatLength).Fill(source[read++]);
                written += repeatLength;
            }
        }

        return written;
    }

    /// <summary>Length of the run of identical bytes starting at <paramref name="offset"/>.</summary>
    private static int MeasureRun(ReadOnlySpan<byte> source, int offset)
    {
        byte value = source[offset];
        int length = 1;

        while (offset + length < source.Length
               && length < MaxRepeatRun
               && source[offset + length] == value)
        {
            length++;
        }

        return length;
    }

    /// <summary>Cheap lookahead: is a repeat run worth breaking the literal group for?</summary>
    private static bool StartsRun(ReadOnlySpan<byte> source, int offset) =>
        offset + MinRepeatRun <= source.Length
        && source[offset] == source[offset + 1]
        && source[offset + 1] == source[offset + 2];
}
