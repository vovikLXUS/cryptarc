namespace CryptArc.Core.Transforms;

/// <summary>
/// Zero-run encoding (RLE0) of move-to-front output, after bzip2's RUNA/RUNB scheme,
/// fitted to a byte alphabet.
/// </summary>
/// <remarks>
/// <para>
/// Move-to-front output after a Burrows–Wheeler transform is mostly zeros, often in
/// long runs. A Huffman coder spends at least one bit per symbol, so a run of m zeros
/// costs at least m bits; this stage shrinks it to about log₂ m symbols first.
/// </para>
/// <para>Stream format — one symbol per byte:</para>
/// <list type="bullet">
///   <item><description>
///     <c>0</c> and <c>1</c> (RUNA, RUNB) — digits of a zero-run length in bijective
///     base 2, least significant first: digit d at position p adds <c>(d + 1)·2^p</c>.
///     A maximal sequence of digits is one run, so m zeros take ⌊log₂(m + 1)⌋ symbols.
///   </description></item>
///   <item><description>
///     <c>2…254</c> — the non-zero value <c>b − 1</c> (1…253).
///   </description></item>
///   <item><description>
///     <c>255</c> followed by <c>0</c> or <c>1</c> — the value <c>254</c> or <c>255</c>.
///     Shifting every value up by one leaves no room for these two, so they cost an
///     extra byte. They are the least likely move-to-front values.
///   </description></item>
/// </list>
/// <para>
/// Worst case: every input byte becomes at most two output bytes, see
/// <see cref="MaxEncodedSize"/>. Complexity: O(n) time, O(1) space.
/// </para>
/// </remarks>
public static class ZeroRunLength
{
    private const byte RunA = 0;
    private const byte RunB = 1;
    private const byte Escape = 255;
    private const int LargestDirectValue = Escape - 2; // 253

    /// <summary>Upper bound on <see cref="Encode"/> output for <paramref name="sourceLength"/> input bytes.</summary>
    public static int MaxEncodedSize(int sourceLength) =>
        (int)Math.Min(2L * sourceLength, Array.MaxLength);

    /// <summary>Encodes <paramref name="source"/> into <paramref name="destination"/>.</summary>
    /// <returns>The number of bytes written, or -1 when the destination was too small.</returns>
    public static int Encode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int read = 0;
        int written = 0;

        while (read < source.Length)
        {
            byte value = source[read];

            if (value == 0)
            {
                int run = 1;
                while (read + run < source.Length && source[read + run] == 0)
                    run++;
                read += run;

                while (run > 0)
                {
                    if (written >= destination.Length)
                        return -1;

                    if ((run & 1) == 1)
                    {
                        destination[written++] = RunA;
                        run = (run - 1) >> 1;
                    }
                    else
                    {
                        destination[written++] = RunB;
                        run = (run - 2) >> 1;
                    }
                }

                continue;
            }

            read++;

            if (value <= LargestDirectValue)
            {
                if (written >= destination.Length)
                    return -1;

                destination[written++] = (byte)(value + 1);
            }
            else
            {
                if (written + 2 > destination.Length)
                    return -1;

                destination[written++] = Escape;
                destination[written++] = (byte)(value - (LargestDirectValue + 1));
            }
        }

        return written;
    }

    /// <summary>Decodes <paramref name="source"/> into <paramref name="destination"/>.</summary>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="InvalidDataException">The input is malformed, truncated or expands past the destination.</exception>
    public static int Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int read = 0;
        int written = 0;

        while (read < source.Length)
        {
            byte symbol = source[read];

            if (symbol <= RunB)
            {
                // Every digit adds at least its weight, so the run outgrows the
                // destination long before the weight can overflow.
                long run = 0;
                long weight = 1;

                while (read < source.Length && source[read] <= RunB)
                {
                    run += (source[read] + 1) * weight;
                    weight <<= 1;
                    read++;

                    if (run > destination.Length - written)
                        throw new InvalidDataException("RLE0 stream expands past the declared block size.");
                }

                destination.Slice(written, (int)run).Clear();
                written += (int)run;
                continue;
            }

            byte value;

            if (symbol == Escape)
            {
                if (read + 1 >= source.Length)
                    throw new InvalidDataException("Truncated escape in RLE0 stream.");

                byte low = source[read + 1];
                if (low > 1)
                    throw new InvalidDataException("Invalid escape in RLE0 stream.");

                value = (byte)(LargestDirectValue + 1 + low);
                read += 2;
            }
            else
            {
                value = (byte)(symbol - 1);
                read++;
            }

            if (written >= destination.Length)
                throw new InvalidDataException("RLE0 stream expands past the declared block size.");

            destination[written++] = value;
        }

        return written;
    }
}
