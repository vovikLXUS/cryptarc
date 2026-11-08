namespace CryptArc.Core.Analysis;

/// <summary>
/// Counts how often each byte value occurs in a buffer: the order-0 statistics every
/// entropy figure in the project is built from, over the whole input rather than a sample.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CountNaive"/> is the obvious loop, one counter per byte value. Its weak
/// spot is a run of equal bytes: every increment reads the counter the previous one has
/// only just written, so each iteration waits for the last store to land — a
/// store-to-load dependency the CPU cannot overlap.
/// </para>
/// <para>
/// <see cref="Count"/> spreads consecutive bytes over four independent tables and adds
/// them up at the end. Four bytes in a row, even four equal ones, then touch four
/// different memory locations, and the increments run in parallel. The same trick is
/// used by fast entropy coders such as zstd's histogram routine.
/// </para>
/// <para>
/// Both are O(n) with a 256-entry table; <see cref="Count"/> uses 4 KiB of stack
/// scratch. They must produce identical counts (hard rule 4).
/// </para>
/// </remarks>
public static class ByteHistogram
{
    /// <summary>Number of counters: one per byte value.</summary>
    public const int Size = 256;

    /// <summary>Counts each byte value in <paramref name="data"/>, four tables at a time.</summary>
    /// <param name="data">The bytes to count.</param>
    /// <param name="counts">Receives 256 counts; overwritten, not accumulated.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="counts"/> is shorter than 256.</exception>
    public static void Count(ReadOnlySpan<byte> data, Span<long> counts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(counts.Length, Size, nameof(counts));

        // Four interleaved tables: table k counts bytes at positions ≡ k (mod 4).
        Span<int> tables = stackalloc int[4 * Size];
        tables.Clear();

        Span<int> t0 = tables[..Size];
        Span<int> t1 = tables.Slice(Size, Size);
        Span<int> t2 = tables.Slice(2 * Size, Size);
        Span<int> t3 = tables.Slice(3 * Size, Size);

        counts[..Size].Clear();

        // int counters could overflow past 2^31 bytes per table, so flush into the long
        // counts every chunk. 2^30 bytes per chunk keeps each table under 2^28.
        const int chunk = 1 << 30;
        while (!data.IsEmpty)
        {
            ReadOnlySpan<byte> part = data.Length > chunk ? data[..chunk] : data;
            data = data[part.Length..];

            int i = 0;
            for (; i + 4 <= part.Length; i += 4)
            {
                t0[part[i]]++;
                t1[part[i + 1]]++;
                t2[part[i + 2]]++;
                t3[part[i + 3]]++;
            }

            for (; i < part.Length; i++)
                t0[part[i]]++;

            for (int value = 0; value < Size; value++)
                counts[value] += (long)t0[value] + t1[value] + t2[value] + t3[value];

            tables.Clear();
        }
    }

    /// <summary>
    /// The naive counterpart of <see cref="Count"/>: one table, one increment per byte.
    /// </summary>
    /// <param name="data">The bytes to count.</param>
    /// <param name="counts">Receives 256 counts; overwritten, not accumulated.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="counts"/> is shorter than 256.</exception>
    public static void CountNaive(ReadOnlySpan<byte> data, Span<long> counts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(counts.Length, Size, nameof(counts));

        counts[..Size].Clear();
        foreach (byte value in data)
            counts[value]++;
    }
}
