using System.Runtime.CompilerServices;

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
/// <para>Two implementations, kept side by side so the speedup can be measured:</para>
/// <list type="bullet">
///   <item><description>
///     <see cref="EncodeNaive"/> / <see cref="DecodeNaive"/> — a byte-by-byte search
///     and a byte-by-byte shift.
///   </description></item>
///   <item><description>
///     <see cref="Encode"/> / <see cref="Decode"/> — the same list, with a check of the
///     front before any search, a vectorised <c>IndexOf</c>, and a block move for long
///     shifts only. After a BWT most indices are 0 or tiny, and there the per-call cost
///     of <c>IndexOf</c> and a block move loses to a plain loop; on random input, where
///     the average index is about 128, the vectorised path wins.
///   </description></item>
/// </list>
/// <para>
/// Both keep the list in a 256-byte array rather than a linked list. Complexity:
/// O(n·k) time in the worst case, k ≤ 256, close to O(n) on typical input where most
/// positions are small. O(1) space, on the stack. Both directions work in place.
/// </para>
/// </remarks>
public static class MoveToFront
{
    private const int ShortShift = 16;

    /// <summary>Replaces every byte of <paramref name="data"/> by its move-to-front index.</summary>
    public static void Encode(Span<byte> data)
    {
        Span<byte> order = stackalloc byte[256];
        InitialiseOrder(order);

        for (int i = 0; i < data.Length; i++)
        {
            byte value = data[i];

            // After a BWT most bytes repeat the previous one: skip the search entirely.
            if (order[0] == value)
            {
                data[i] = 0;
                continue;
            }

            int index = order.IndexOf(value);
            Shift(order, index);
            order[0] = value;
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
                Shift(order, index);
                order[0] = value;
            }

            data[i] = value;
        }
    }

    /// <summary>
    /// Same result as <see cref="Encode"/>, with a plain loop for the search and the
    /// shift. Kept as the benchmark baseline.
    /// </summary>
    public static void EncodeNaive(Span<byte> data)
    {
        Span<byte> order = stackalloc byte[256];
        InitialiseOrder(order);

        for (int i = 0; i < data.Length; i++)
        {
            byte value = data[i];

            int index = 0;
            while (order[index] != value)
                index++;

            for (int j = index; j > 0; j--)
                order[j] = order[j - 1];
            order[0] = value;

            data[i] = (byte)index;
        }
    }

    /// <summary>
    /// Same result as <see cref="Decode"/>, with a plain loop for the shift. Kept as the
    /// benchmark baseline.
    /// </summary>
    public static void DecodeNaive(Span<byte> data)
    {
        Span<byte> order = stackalloc byte[256];
        InitialiseOrder(order);

        for (int i = 0; i < data.Length; i++)
        {
            int index = data[i];
            byte value = order[index];

            for (int j = index; j > 0; j--)
                order[j] = order[j - 1];
            order[0] = value;

            data[i] = value;
        }
    }

    /// <summary>
    /// Moves <c>order[0..index)</c> one place right. Short shifts are a loop, because
    /// most indices after a BWT are tiny and a block-move call costs more than it saves;
    /// long ones are a single block move.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Shift(Span<byte> order, int index)
    {
        if (index < ShortShift)
        {
            for (int j = index; j > 0; j--)
                order[j] = order[j - 1];
        }
        else
        {
            order[..index].CopyTo(order[1..]);
        }
    }

    private static void InitialiseOrder(Span<byte> order)
    {
        for (int i = 0; i < order.Length; i++)
            order[i] = (byte)i;
    }
}
