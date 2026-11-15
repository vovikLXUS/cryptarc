namespace CryptArc.Core.Sarc;

/// <summary>How blocks are chosen for each volume.</summary>
public enum SarcPacking
{
    /// <summary>Exact 0/1 knapsack by dynamic programming, O(n·W) per volume.</summary>
    Knapsack,

    /// <summary>Greedy by value per byte, the baseline the knapsack is compared with.</summary>
    Greedy,
}

/// <summary>A block waiting to be placed in a volume.</summary>
/// <param name="File">Index of its file in the archive-wide file list.</param>
/// <param name="Weight"><c>coded_len + 15</c>: its data plus its block-table row.</param>
/// <param name="Value">The priority of its file's type.</param>
internal readonly record struct SarcPackItem(int File, int Weight, int Value);

/// <summary>What goes into one volume.</summary>
/// <param name="Files">Archive-wide indices of the files in the volume's file table, ascending.</param>
/// <param name="Blocks">Indices of the items placed in the volume, ascending.</param>
internal sealed record SarcVolumePlan(int[] Files, int[] Blocks);

/// <summary>
/// Splits blocks across volumes as FORMAT_SPEC_P2 §7 prescribes. Before each volume the
/// file table is fixed — every file that still has an unplaced block — and reserved in
/// full, so the capacity <c>W = max_volume_bytes − 41 − 32 − 6 − table</c> is known before
/// the knapsack runs. The knapsack then picks the blocks of greatest total value whose
/// weights fit; the rest move on to the next volume.
/// </summary>
internal static class SarcVolumePacker
{
    /// <summary>
    /// Largest DP table, in cells, before a volume falls back to the greedy choice. The
    /// table holds one bit per cell for the reconstruction, so this is 8 MiB.
    /// </summary>
    public const long MaxDpCells = 1L << 26;

    private const int VolumeOverhead = SarcVolume.HeaderSize + SarcVolume.TagSize + SarcPayload.FixedFieldsSize;

    /// <summary>Plans every volume.</summary>
    /// <param name="fileRecordSizes">Bytes of the file-table row of each file.</param>
    /// <param name="items">Every block of every file.</param>
    /// <param name="maxVolumeBytes">The packer's limit on a whole volume.</param>
    /// <param name="packing">How each volume's blocks are chosen.</param>
    /// <returns>One plan per volume, in order.</returns>
    /// <exception cref="ArgumentException">
    /// The file table alone does not fit, or a block is larger than the capacity left for it.
    /// </exception>
    public static List<SarcVolumePlan> Plan(
        IReadOnlyList<int> fileRecordSizes, IReadOnlyList<SarcPackItem> items, int maxVolumeBytes, SarcPacking packing)
    {
        var plans = new List<SarcVolumePlan>();

        // No files: still one volume, file_count = block_count = 0, so the archive exists
        // and reads back as empty (criterion C2: "коректна робота на порожньому архіві").
        if (items.Count == 0)
        {
            if (maxVolumeBytes < VolumeOverhead)
                throw new ArgumentException($"A {maxVolumeBytes}-byte volume cannot hold even an empty payload.");

            plans.Add(new SarcVolumePlan([], []));
            return plans;
        }

        var pending = new List<int>(items.Count);
        for (int i = 0; i < items.Count; i++)
            pending.Add(i);

        while (pending.Count > 0)
        {
            var files = new SortedSet<int>();
            foreach (int item in pending)
                files.Add(items[item].File);

            long tableSize = 0;
            foreach (int file in files)
                tableSize += fileRecordSizes[file];

            long capacity = maxVolumeBytes - VolumeOverhead - tableSize;
            if (capacity < 0)
                throw new ArgumentException(
                    $"The file table of {files.Count} files ({tableSize} bytes) does not fit into a {maxVolumeBytes}-byte volume. " +
                    "Increase max_volume_bytes or pack fewer files at a time.");

            int[] weights = new int[pending.Count];
            int[] values = new int[pending.Count];
            for (int i = 0; i < pending.Count; i++)
            {
                SarcPackItem item = items[pending[i]];
                if (item.Weight > capacity)
                    throw new ArgumentException(
                        $"A coded block of {item.Weight - SarcPayload.BlockRecordSize} bytes does not fit into the {capacity} bytes a volume has left. " +
                        "Decrease block_size or increase max_volume_bytes.");

                weights[i] = item.Weight;
                // A zero priority would let the knapsack take nothing and the loop never end.
                values[i] = Math.Max(1, item.Value);
            }

            bool[] chosen = packing == SarcPacking.Knapsack
                ? Knapsack(weights, values, (int)capacity)
                : Greedy(weights, values, (int)capacity);

            var placed = new List<int>();
            var remaining = new List<int>();
            for (int i = 0; i < pending.Count; i++)
                (chosen[i] ? placed : remaining).Add(pending[i]);

            // The table stays as reserved, files without a block in this volume included.
            // §7: "Змінювати склад таблиці після вибору блоків не можна" — trimming it would
            // pack a volume for a capacity other than the one the knapsack solved for.
            // The issued ref_06 lists only files that have blocks; readers accept both.
            plans.Add(new SarcVolumePlan([.. files], [.. placed]));
            pending = remaining;
        }

        return plans;
    }

    /// <summary>
    /// Exact 0/1 knapsack, O(n·W) time. Falls back to <see cref="Greedy"/> when the table
    /// would exceed <see cref="MaxDpCells"/>, and takes everything when everything fits.
    /// </summary>
    /// <param name="weights">Item weights, all at most <paramref name="capacity"/>.</param>
    /// <param name="values">Item values.</param>
    /// <param name="capacity">The knapsack's capacity.</param>
    /// <returns>Which items are taken.</returns>
    public static bool[] Knapsack(ReadOnlySpan<int> weights, ReadOnlySpan<int> values, int capacity)
    {
        int count = weights.Length;
        if (FitsWhole(weights, capacity))
            return All(count);

        long cells = (long)count * (capacity + 1);
        if (cells > MaxDpCells)
            return Greedy(weights, values, capacity);

        long[] best = new long[capacity + 1];
        ulong[] taken = new ulong[(cells + 63) / 64];

        for (int i = 0; i < count; i++)
        {
            int weight = weights[i];
            long value = values[i];
            long row = (long)i * (capacity + 1);

            for (int w = capacity; w >= weight; w--)
            {
                long candidate = best[w - weight] + value;
                if (candidate > best[w])
                {
                    best[w] = candidate;
                    long bit = row + w;
                    taken[bit >> 6] |= 1UL << (int)(bit & 63);
                }
            }
        }

        var chosen = new bool[count];
        int left = capacity;
        for (int i = count - 1; i >= 0; i--)
        {
            long bit = (long)i * (capacity + 1) + left;
            if ((taken[bit >> 6] >> (int)(bit & 63) & 1) != 0)
            {
                chosen[i] = true;
                left -= weights[i];
            }
        }

        return chosen;
    }

    /// <summary>Greedy: items by falling value per byte, each taken if it still fits.</summary>
    /// <param name="weights">Item weights.</param>
    /// <param name="values">Item values.</param>
    /// <param name="capacity">The knapsack's capacity.</param>
    /// <returns>Which items are taken.</returns>
    public static bool[] Greedy(ReadOnlySpan<int> weights, ReadOnlySpan<int> values, int capacity)
    {
        int count = weights.Length;
        if (FitsWhole(weights, capacity))
            return All(count);

        int[] order = new int[count];
        for (int i = 0; i < count; i++)
            order[i] = i;

        int[] w = weights.ToArray();
        int[] v = values.ToArray();
        // value/weight descending, compared by cross-multiplication; ties keep input order.
        Array.Sort(order, (a, b) =>
        {
            int byRatio = ((long)v[b] * w[a]).CompareTo((long)v[a] * w[b]);
            return byRatio != 0 ? byRatio : a.CompareTo(b);
        });

        var chosen = new bool[count];
        long left = capacity;
        foreach (int i in order)
        {
            if (w[i] <= left)
            {
                chosen[i] = true;
                left -= w[i];
            }
        }

        return chosen;
    }

    private static bool FitsWhole(ReadOnlySpan<int> weights, int capacity)
    {
        long total = 0;
        foreach (int weight in weights)
            total += weight;

        return total <= capacity;
    }

    private static bool[] All(int count)
    {
        var all = new bool[count];
        Array.Fill(all, true);
        return all;
    }
}
