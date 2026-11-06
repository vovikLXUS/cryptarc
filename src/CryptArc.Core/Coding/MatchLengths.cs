using System.Buffers.Binary;
using System.Numerics;

namespace CryptArc.Core.Coding;

/// <summary>
/// Common-prefix length of two byte sequences — the innermost measurement of every
/// Lempel-Ziv match search, and the hottest loop in the project.
/// </summary>
/// <remarks>
/// <see cref="Wide"/> and <see cref="Bytewise"/> must always agree; they are the
/// optimised and naive halves of the same operation, kept side by side so the speedup
/// can be measured (hard rule 4).
/// </remarks>
internal static class MatchLengths
{
    /// <summary>
    /// Counts how many leading bytes <paramref name="left"/> and <paramref name="right"/>
    /// share, comparing 8 bytes at a time.
    /// </summary>
    /// <param name="left">The first sequence.</param>
    /// <param name="right">The second sequence.</param>
    /// <param name="limit">The largest count to report; both spans must be at least this long.</param>
    /// <returns>The length of the common prefix, at most <paramref name="limit"/>.</returns>
    /// <remarks>
    /// XOR two <see cref="ulong"/> words and the trailing-zero count of the result names
    /// the first differing byte, so one comparison settles eight positions.
    /// </remarks>
    internal static int Wide(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, int limit)
    {
        int length = 0;

        while (limit - length >= sizeof(ulong))
        {
            ulong difference = BinaryPrimitives.ReadUInt64LittleEndian(left[length..])
                               ^ BinaryPrimitives.ReadUInt64LittleEndian(right[length..]);
            if (difference != 0)
                return length + BitOperations.TrailingZeroCount(difference) / 8;

            length += sizeof(ulong);
        }

        while (length < limit && left[length] == right[length])
            length++;

        return length;
    }

    /// <summary>
    /// Counts how many leading bytes <paramref name="left"/> and <paramref name="right"/>
    /// share, comparing one byte at a time. The baseline for <see cref="Wide"/>.
    /// </summary>
    /// <param name="left">The first sequence.</param>
    /// <param name="right">The second sequence.</param>
    /// <param name="limit">The largest count to report; both spans must be at least this long.</param>
    /// <returns>The length of the common prefix, at most <paramref name="limit"/>.</returns>
    internal static int Bytewise(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, int limit)
    {
        int length = 0;

        while (length < limit && left[length] == right[length])
            length++;

        return length;
    }
}
