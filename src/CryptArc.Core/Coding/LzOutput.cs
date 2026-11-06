namespace CryptArc.Core.Coding;

/// <summary>Output-side half of Lempel-Ziv decoding: expanding a back-reference.</summary>
internal static class LzOutput
{
    /// <summary>
    /// Copies <paramref name="length"/> bytes from <paramref name="distance"/> bytes back in
    /// <paramref name="output"/> to <paramref name="written"/>, correctly when the source
    /// overlaps the destination.
    /// </summary>
    /// <param name="output">The buffer being decoded into.</param>
    /// <param name="written">How many bytes are already decoded; where the copy lands.</param>
    /// <param name="distance">How far back the match starts, 1-based.</param>
    /// <param name="length">How many bytes to copy.</param>
    /// <remarks>
    /// The distance may be smaller than the length. The copy then overlaps its own output,
    /// which is how a run of one byte is expressed as a single match with distance 1, so it
    /// must proceed forwards one byte at a time rather than as a block move. Callers check
    /// that <paramref name="distance"/> does not reach before the start of the buffer and
    /// that <paramref name="length"/> fits.
    /// </remarks>
    internal static void CopyMatch(Span<byte> output, int written, int distance, int length)
    {
        int from = written - distance;

        if (distance >= length)
        {
            output.Slice(from, length).CopyTo(output[written..]);
        }
        else if (distance == 1)
        {
            output.Slice(written, length).Fill(output[from]);
        }
        else
        {
            for (int i = 0; i < length; i++)
                output[written + i] = output[from + i];
        }
    }
}
