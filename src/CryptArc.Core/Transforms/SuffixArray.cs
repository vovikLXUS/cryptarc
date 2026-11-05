using System.Buffers;

namespace CryptArc.Core.Transforms;

/// <summary>
/// Suffix array construction: the positions of all suffixes of a text, in
/// lexicographic order. The sorting step of the Burrows–Wheeler transform.
/// </summary>
/// <remarks>
/// <para>
/// Order is plain byte order, with a shorter suffix sorting before any longer one it
/// is a prefix of. That is the same as appending a sentinel smaller than every byte,
/// which is what <see cref="BurrowsWheeler"/> relies on.
/// </para>
/// <para>Two implementations, kept side by side so the speedup can be measured:</para>
/// <list type="bullet">
///   <item><description>
///     <see cref="BuildNaive"/> — comparison sort of the suffixes. O(n log n)
///     comparisons of up to n bytes each: O(n² log n) on repetitive input such as text
///     or a constant run, close to O(n log n) on random bytes where comparisons end early.
///   </description></item>
///   <item><description>
///     <see cref="Build"/> — prefix doubling (Manber–Myers) with radix sort. O(n log n)
///     time regardless of content, O(n) additional space.
///   </description></item>
/// </list>
/// </remarks>
public static class SuffixArray
{
    /// <summary>
    /// Builds the suffix array of <paramref name="text"/> by prefix doubling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// After round <c>k</c> every suffix carries a rank that orders it by its first
    /// <c>k</c> bytes. The next round sorts by the pair (rank of <c>i</c>, rank of
    /// <c>i + k</c>), which orders by the first <c>2k</c> bytes. Both keys are below n,
    /// so each round is two linear counting-sort passes instead of a comparison sort.
    /// It stops as soon as all ranks are distinct — after at most ⌈log₂ n⌉ rounds.
    /// </para>
    /// <para>Complexity: O(n log n) time, four pooled int arrays of length n.</para>
    /// </remarks>
    /// <param name="text">The text to index.</param>
    /// <param name="suffixArray">Receives the sorted suffix positions; at least <c>text.Length</c> long.</param>
    public static void Build(ReadOnlySpan<byte> text, Span<int> suffixArray)
    {
        int n = text.Length;
        ArgumentOutOfRangeException.ThrowIfLessThan(suffixArray.Length, n, nameof(suffixArray));

        if (n == 0)
            return;

        Span<int> sa = suffixArray[..n];

        int[] rankBuffer = ArrayPool<int>.Shared.Rent(n);
        int[] nextRankBuffer = ArrayPool<int>.Shared.Rent(n);
        int[] orderBuffer = ArrayPool<int>.Shared.Rent(n);
        int[] countBuffer = ArrayPool<int>.Shared.Rent(Math.Max(n, 256));

        try
        {
            Span<int> rank = rankBuffer.AsSpan(0, n);
            Span<int> nextRank = nextRankBuffer.AsSpan(0, n);
            Span<int> order = orderBuffer.AsSpan(0, n);
            Span<int> count = countBuffer.AsSpan(0, Math.Max(n, 256));

            // Round 0: counting sort by the first byte.
            count[..256].Clear();
            foreach (byte value in text)
                count[value]++;

            ByteBuckets.CountsToStarts(count, 0);

            for (int i = 0; i < n; i++)
                sa[count[text[i]]++] = i;

            int classes = 1;
            rank[sa[0]] = 0;
            for (int j = 1; j < n; j++)
            {
                if (text[sa[j]] != text[sa[j - 1]])
                    classes++;
                rank[sa[j]] = classes - 1;
            }

            for (int k = 1; classes < n; k <<= 1)
            {
                // Order by the second key, rank[i + k]. Suffixes shorter than k + 1 have
                // no second half, so they sort first; the rest follow in the order of the
                // suffix k positions later, which the previous round already sorted.
                int filled = 0;
                for (int i = n - k; i < n; i++)
                    order[filled++] = i;
                for (int j = 0; j < n; j++)
                {
                    if (sa[j] >= k)
                        order[filled++] = sa[j] - k;
                }

                // Stable counting sort by the first key, rank[i].
                count[..classes].Clear();
                for (int i = 0; i < n; i++)
                    count[rank[i]]++;
                for (int c = 1; c < classes; c++)
                    count[c] += count[c - 1];
                for (int j = n - 1; j >= 0; j--)
                    sa[--count[rank[order[j]]]] = order[j];

                // Re-rank: neighbours share a rank only if both halves are equal.
                classes = 1;
                nextRank[sa[0]] = 0;
                for (int j = 1; j < n; j++)
                {
                    int current = sa[j];
                    int previous = sa[j - 1];

                    if (rank[current] != rank[previous]
                        || SecondKey(rank, current, k) != SecondKey(rank, previous, k))
                    {
                        classes++;
                    }

                    nextRank[current] = classes - 1;
                }

                Span<int> swap = rank;
                rank = nextRank;
                nextRank = swap;
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rankBuffer);
            ArrayPool<int>.Shared.Return(nextRankBuffer);
            ArrayPool<int>.Shared.Return(orderBuffer);
            ArrayPool<int>.Shared.Return(countBuffer);
        }
    }

    /// <summary>
    /// Builds the suffix array by sorting the suffixes with a byte-by-byte comparison.
    /// Same result as <see cref="Build"/>; kept as the benchmark baseline and as the
    /// correctness oracle in the tests.
    /// </summary>
    /// <remarks>Complexity: O(n² log n) time in the worst case, one copy of the text.</remarks>
    /// <param name="text">The text to index.</param>
    /// <param name="suffixArray">Receives the sorted suffix positions; at least <c>text.Length</c> long.</param>
    public static void BuildNaive(ReadOnlySpan<byte> text, Span<int> suffixArray)
    {
        int n = text.Length;
        ArgumentOutOfRangeException.ThrowIfLessThan(suffixArray.Length, n, nameof(suffixArray));

        // The comparison delegate cannot capture a span, so it works on a copy.
        byte[] copy = text.ToArray();
        Span<int> sa = suffixArray[..n];

        for (int i = 0; i < n; i++)
            sa[i] = i;

        sa.Sort((a, b) => copy.AsSpan(a).SequenceCompareTo(copy.AsSpan(b)));
    }

    private static int SecondKey(Span<int> rank, int position, int k) =>
        position + k < rank.Length ? rank[position + k] : -1;
}
