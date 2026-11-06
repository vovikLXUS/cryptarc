namespace CryptArc.Core.Coding;

/// <summary>The longest match a finder could see from one position.</summary>
/// <param name="Length">Length of the match in bytes, or 0 when there is none.</param>
/// <param name="Distance">How many bytes back the match starts, 1-based.</param>
internal readonly record struct Match(int Length, int Distance)
{
    /// <summary>No match at this position.</summary>
    internal static Match None => default;
}

/// <summary>
/// A match-search strategy. A struct constraint on the caller lets the JIT specialise
/// the parser per strategy, with no virtual calls in the loop.
/// </summary>
/// <remarks>
/// Implementations may be stateful across calls, so a finder belongs to one encode pass
/// and is never shared between threads.
/// </remarks>
internal interface IMatchFinder
{
    /// <summary>
    /// Finds the longest match at <paramref name="position"/>, preferring the nearest on a tie.
    /// </summary>
    /// <param name="source">The whole input being compressed.</param>
    /// <param name="position">Where to look from.</param>
    /// <returns>The match, or <see cref="Match.None"/> when none reaches the minimum length.</returns>
    /// <remarks>
    /// Positions are asked for in increasing order, and each one at most twice in a row —
    /// a lazy-matching parser looks one byte ahead before committing.
    /// </remarks>
    Match Find(ReadOnlySpan<byte> source, int position);
}

/// <summary>
/// The O(n·W) baseline: compare every position in the window, byte by byte, and take the
/// longest match greedily. Kept as the benchmark baseline and as a correctness oracle for
/// <see cref="HashChainFinder"/>.
/// </summary>
/// <param name="window">Largest distance a match may reach back.</param>
/// <param name="minMatch">Shortest match worth reporting.</param>
/// <param name="maxMatch">Longest match the caller's format can express.</param>
internal readonly struct LinearScanFinder(int window, int minMatch, int maxMatch) : IMatchFinder
{
    /// <inheritdoc/>
    public Match Find(ReadOnlySpan<byte> source, int position)
    {
        int limit = Math.Min(maxMatch, source.Length - position);
        if (limit < minMatch)
            return Match.None;

        ReadOnlySpan<byte> ahead = source.Slice(position, limit);
        int bestLength = minMatch - 1;
        int bestDistance = 0;
        int oldest = Math.Max(0, position - window);

        for (int candidate = position - 1; candidate >= oldest; candidate--)
        {
            int length = MatchLengths.Bytewise(source.Slice(candidate, limit), ahead, limit);
            if (length > bestLength)
            {
                bestLength = length;
                bestDistance = position - candidate;
                if (length == limit)
                    break;
            }
        }

        return bestDistance == 0 ? Match.None : new Match(bestLength, bestDistance);
    }
}

/// <summary>
/// Hash chains: only positions whose first 3 bytes hash alike are compared, newest first,
/// and at most <paramref name="maxChain"/> of them.
/// </summary>
/// <param name="head">
/// Newest position per hash bucket; must hold <see cref="HashSize"/> entries, all set to
/// <see cref="NoPosition"/> before the first call.
/// </param>
/// <param name="prev">
/// Links each position, modulo <paramref name="window"/>, to the previous one in its chain;
/// must hold <paramref name="window"/> entries.
/// </param>
/// <param name="window">
/// Largest distance a match may reach back. Must be a power of two — it masks into
/// <paramref name="prev"/>.
/// </param>
/// <param name="minMatch">Shortest match worth reporting.</param>
/// <param name="maxMatch">Longest match the caller's format can express.</param>
/// <param name="maxChain">
/// Largest number of earlier positions compared for one match. Larger values find longer
/// matches more slowly; <see cref="int.MaxValue"/> searches the whole window, which makes
/// this finder agree with <see cref="LinearScanFinder"/> byte for byte.
/// </param>
/// <remarks>
/// A position is linked into its chain only after it has been searched from, so a chain
/// never reaches the current position.
/// </remarks>
internal struct HashChainFinder(
    int[] head, int[] prev, int window, int minMatch, int maxMatch, int maxChain) : IMatchFinder
{
    /// <summary>Bits of hash, and so the size of <c>head</c> as a power of two.</summary>
    private const int HashBits = 15;

    /// <summary>Required length of the <c>head</c> array.</summary>
    internal const int HashSize = 1 << HashBits;

    /// <summary>Empty-bucket sentinel that <c>head</c> must be filled with.</summary>
    internal const int NoPosition = -1;

    /// <summary>How many bytes the hash covers.</summary>
    private const int HashedBytes = 3;

    // Every position below this one is already linked into the chains.
    private int _inserted;

    /// <inheritdoc/>
    public Match Find(ReadOnlySpan<byte> source, int position)
    {
        InsertUpTo(source, position);

        int limit = Math.Min(maxMatch, source.Length - position);
        if (limit < minMatch)
            return Match.None;

        ReadOnlySpan<byte> ahead = source.Slice(position, limit);
        int bestLength = minMatch - 1;
        int bestDistance = 0;
        int oldest = position - window;
        int mask = window - 1;
        int candidate = head[Hash(source, position)];

        for (int steps = maxChain; candidate >= 0 && candidate >= oldest && steps > 0; steps--)
        {
            // Cheap reject: a longer match must also agree on the byte just past the best one.
            if (source[candidate + bestLength] == ahead[bestLength])
            {
                int length = MatchLengths.Wide(source.Slice(candidate, limit), ahead, limit);
                if (length > bestLength)
                {
                    bestLength = length;
                    bestDistance = position - candidate;
                    if (length == limit)
                        break;
                }
            }

            int next = prev[candidate & mask];
            if (next >= candidate)
                break;

            candidate = next;
        }

        return bestDistance == 0 ? Match.None : new Match(bestLength, bestDistance);
    }

    /// <summary>Links every position before <paramref name="end"/> into its chain.</summary>
    private void InsertUpTo(ReadOnlySpan<byte> source, int end)
    {
        int last = Math.Min(end, source.Length - HashedBytes + 1);
        int mask = window - 1;

        for (; _inserted < last; _inserted++)
        {
            int hash = Hash(source, _inserted);
            prev[_inserted & mask] = head[hash];
            head[hash] = _inserted;
        }

        if (_inserted < end)
            _inserted = end;
    }

    private static int Hash(ReadOnlySpan<byte> source, int position)
    {
        uint key = source[position] | (uint)source[position + 1] << 8 | (uint)source[position + 2] << 16;
        return (int)((key * 0x9E3779B1u) >> (32 - HashBits));
    }
}
