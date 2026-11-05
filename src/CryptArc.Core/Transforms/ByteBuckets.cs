namespace CryptArc.Core.Transforms;

/// <summary>Bucket arithmetic shared by the counting sorts over byte values.</summary>
internal static class ByteBuckets
{
    /// <summary>
    /// Turns 256 byte counts into the first position of each byte's bucket, in place:
    /// an exclusive prefix sum starting at <paramref name="firstPosition"/>.
    /// </summary>
    public static void CountsToStarts(Span<int> counts, int firstPosition)
    {
        int sum = firstPosition;
        for (int c = 0; c < 256; c++)
        {
            int bucket = counts[c];
            counts[c] = sum;
            sum += bucket;
        }
    }
}
