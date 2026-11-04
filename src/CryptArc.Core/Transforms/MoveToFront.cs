namespace CryptArc.Core.Transforms;

/// <summary>
/// Move-to-front coding: replaces each byte by its position in a list of recently
/// used bytes, then moves it to the front of that list.
/// </summary>
/// <remarks>
/// <para>
/// The list starts as 0, 1, …, 255. After the Burrows–Wheeler transform the same
/// byte tends to repeat in clusters, so the output is dominated by small values and
/// zeros, which the entropy coder then encodes in few bits.
/// </para>
/// <para>
/// The list is a 256-byte array rather than a linked list: the search is a
/// vectorised <c>IndexOf</c> and the shift a single block move. Complexity: O(n·k)
/// time in the worst case, k ≤ 256, close to O(n) on typical input where most
/// positions are small. O(1) space, on the stack. Both directions work in place.
/// </para>
/// </remarks>
public static class MoveToFront
{
    /// <summary>Replaces every byte of <paramref name="data"/> by its move-to-front index.</summary>
    public static void Encode(Span<byte> data)
    {
        Span<byte> order = stackalloc byte[256];
        InitialiseOrder(order);

        for (int i = 0; i < data.Length; i++)
        {
            byte value = data[i];
            int index = order.IndexOf(value);

            if (index > 0)
            {
                order[..index].CopyTo(order[1..]);
                order[0] = value;
            }

            data[i] = (byte)index;
        }
    }

    /// <summary>Inverse of <see cref="Encode"/>: replaces every index by the byte it names.</summary>
    public static void Decode(Span<byte> data)
    {
        Span<byte> order = stackalloc byte[256];
        InitialiseOrder(order);

        for (int i = 0; i < data.Length; i++)
        {
            int index = data[i];
            byte value = order[index];

            if (index > 0)
            {
                order[..index].CopyTo(order[1..]);
                order[0] = value;
            }

            data[i] = value;
        }
    }

    private static void InitialiseOrder(Span<byte> order)
    {
        for (int i = 0; i < order.Length; i++)
            order[i] = (byte)i;
    }
}
