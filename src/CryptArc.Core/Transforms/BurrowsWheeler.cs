using System.Buffers;

namespace CryptArc.Core.Transforms;

/// <summary>
/// The Burrows–Wheeler transform: a reversible permutation of a block that groups
/// bytes followed by similar contexts, turning long-range redundancy into short runs.
/// </summary>
/// <remarks>
/// <para>
/// Conceptually the text gets a sentinel <c>$</c>, smaller than every byte, and all
/// n + 1 rotations of <c>text$</c> are sorted. The output is the last column of that
/// sorted matrix. Because of the sentinel, sorting rotations is the same as sorting
/// suffixes, so the matrix is never built: <see cref="SuffixArray"/> gives the order.
/// </para>
/// <para>
/// The last column has n + 1 entries, exactly one of which is the sentinel. It is
/// dropped from the output and its row is returned instead as the primary index,
/// in the range 1…n (row 0 is always the rotation that starts with <c>$</c>).
/// </para>
/// <para>
/// Complexity: forward is the suffix sort plus O(n); inverse is O(n) through the
/// LF mapping. Both use one pooled int array of length n.
/// </para>
/// </remarks>
public static class BurrowsWheeler
{
    /// <summary>
    /// Computes the last column of the sorted rotation matrix, without the sentinel.
    /// </summary>
    /// <param name="source">The block to transform.</param>
    /// <param name="lastColumn">Receives <c>source.Length</c> bytes.</param>
    /// <returns>The row of the sentinel: 1…n, or 0 for empty input.</returns>
    public static int Forward(ReadOnlySpan<byte> source, Span<byte> lastColumn)
    {
        int n = source.Length;
        ArgumentOutOfRangeException.ThrowIfLessThan(lastColumn.Length, n, nameof(lastColumn));

        if (n == 0)
            return 0;

        int[] suffixBuffer = ArrayPool<int>.Shared.Rent(n);

        try
        {
            Span<int> sa = suffixBuffer.AsSpan(0, n);
            SuffixArray.Build(source, sa);

            // Row 0 is the lone sentinel suffix; the byte before it is the last byte.
            lastColumn[0] = source[n - 1];

            int primaryIndex = 0;
            int written = 1;

            for (int row = 0; row < n; row++)
            {
                int position = sa[row];

                if (position == 0)
                    primaryIndex = row + 1;
                else
                    lastColumn[written++] = source[position - 1];
            }

            return primaryIndex;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(suffixBuffer);
        }
    }

    /// <summary>
    /// Restores the original block from its last column and primary index.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The LF mapping sends a row to the row of the rotation one step to the left: the
    /// k-th occurrence of byte c in the last column is the k-th occurrence of c in the
    /// sorted first column. Starting at row 0 (the rotation <c>$text</c>), each step
    /// yields the previous byte of the text, so it is rebuilt back to front.
    /// </para>
    /// <para>
    /// A valid input walks one cycle through all n + 1 rows and meets the primary row
    /// only on the last step. Anything else means the input is corrupt.
    /// </para>
    /// </remarks>
    /// <param name="lastColumn">The transformed block, sentinel excluded.</param>
    /// <param name="primaryIndex">The value returned by <see cref="Forward"/>.</param>
    /// <param name="destination">Receives <c>lastColumn.Length</c> bytes.</param>
    /// <exception cref="InvalidDataException">The primary index or the column is inconsistent.</exception>
    public static void Inverse(ReadOnlySpan<byte> lastColumn, int primaryIndex, Span<byte> destination)
    {
        int n = lastColumn.Length;
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, n, nameof(destination));

        if (n == 0)
        {
            if (primaryIndex != 0)
                throw new InvalidDataException("BWT primary index is out of range.");
            return;
        }

        if (primaryIndex < 1 || primaryIndex > n)
            throw new InvalidDataException("BWT primary index is out of range.");

        // First row of each byte in the sorted first column; row 0 holds the sentinel.
        Span<int> next = stackalloc int[256];
        next.Clear();
        foreach (byte value in lastColumn)
            next[value]++;

        ByteBuckets.CountsToStarts(next, 1);

        int[] lfBuffer = ArrayPool<int>.Shared.Rent(n);

        try
        {
            // lf[i]: the row reached from the row whose last byte is lastColumn[i].
            Span<int> lf = lfBuffer.AsSpan(0, n);
            for (int i = 0; i < n; i++)
                lf[i] = next[lastColumn[i]]++;

            int row = 0;
            for (int k = n - 1; k >= 0; k--)
            {
                if (row == primaryIndex)
                    throw new InvalidDataException("BWT column does not form a single cycle.");

                int index = row < primaryIndex ? row : row - 1;
                destination[k] = lastColumn[index];
                row = lf[index];
            }

            if (row != primaryIndex)
                throw new InvalidDataException("BWT column does not form a single cycle.");
        }
        finally
        {
            ArrayPool<int>.Shared.Return(lfBuffer);
        }
    }
}
