using System.Buffers.Binary;
using CryptArc.Core.IO;

namespace CryptArc.Core.Codecs;

/// <summary>
/// Canonical Huffman coding over bytes, with code lengths limited to 15 bits.
/// </summary>
/// <remarks>
/// <para>Stream format — an empty input encodes to zero bytes, anything else is:</para>
/// <list type="bullet">
///   <item><description>
///     Bytes 0…3 — number of encoded symbols (the original length), little-endian.
///   </description></item>
///   <item><description>
///     Bytes 4…131 — code length of each of the 256 symbols, 4 bits each. Symbol
///     <c>2i</c> is the low nibble of byte <c>4 + i</c>, symbol <c>2i + 1</c> the high
///     nibble. Zero means the symbol does not occur.
///   </description></item>
///   <item><description>
///     Bytes 132… — the codes, written through <see cref="BitWriter"/> and zero padded
///     up to a whole byte.
///   </description></item>
/// </list>
/// <para>
/// Only the lengths are stored: canonical codes are fully determined by them. Symbols
/// are ordered by (length, value) and numbered consecutively, so the decoder rebuilds
/// the same codes. A canonical code is defined first-bit-is-most-significant while the
/// bit streams are least-significant-first, so every code is bit-reversed before use.
/// </para>
/// <para>
/// The lengths must form a complete prefix code (Kraft sum of exactly 1). The one
/// exception is a single distinct symbol, which gets the 1-bit code <c>0</c>: a tree
/// with one leaf has depth 0, which could not encode anything.
/// </para>
/// <para>
/// Worst case: an optimal code never loses to a flat 8-bit code, so the bit stream
/// takes at most <c>n</c> bytes and only the 132-byte header is overhead. This holds
/// for the limited code too, because a flat 8-bit code fits within the 15-bit limit.
/// </para>
/// <para>
/// Complexity, with k ≤ 256 distinct symbols and L = 15. Encoding: O(n + k log k) time,
/// plus O(k·L) on the rare blocks that need limiting. Decoding: O(n) time with
/// <see cref="Decode"/>, O(n·L) with <see cref="DecodeTreeWalk"/>. Both directions use
/// O(1) additional space, all of it on the stack.
/// </para>
/// </remarks>
public sealed class HuffmanCodec : ICodec
{
    private const int SymbolCount = 256;
    private const int MaxNodes = 2 * SymbolCount - 1;
    private const int MaxCodeLength = 15;

    private const int LengthTableSize = SymbolCount / 2;
    private const int HeaderSize = sizeof(uint) + LengthTableSize;

    // Decoder table layout: 1024 root entries indexed by the next 10 bits, followed by
    // subtables for the longer codes. A subtable covers the remaining 5 bits at most and,
    // the code being complete, holds at least two leaves, so there are at most 128 of them.
    private const int RootBits = 10;
    private const int RootSize = 1 << RootBits;
    private const int MaxSubtableBits = MaxCodeLength - RootBits;
    private const int DecodeTableSize = RootSize + (SymbolCount / 2 << MaxSubtableBits);

    // Decoder table entries: 0 is invalid, a leaf is (symbol << 4) | bits to consume,
    // and a link is LinkFlag | (subtable offset << 4) | subtable index bits.
    private const uint LinkFlag = 0x8000_0000;
    private const int EntryFieldBits = 4;
    private const uint EntryFieldMask = (1u << EntryFieldBits) - 1;

    // Package-merge: a level merges at most k leaves with fewer than k packages.
    private const int MaxLevelItems = 2 * SymbolCount;

    public CodecId Id => CodecId.Huffman;

    public string Name => "Huffman";

    public int MaxCompressedSize(int sourceLength) =>
        sourceLength == 0 ? 0 : sourceLength + HeaderSize;

    public int Encode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty)
            return 0;
        if (destination.Length < HeaderSize)
            return -1;

        Span<int> frequencies = stackalloc int[SymbolCount];
        frequencies.Clear();

        foreach (byte value in source)
            frequencies[value]++;

        Span<byte> lengths = stackalloc byte[SymbolCount];
        BuildCodeLengths(frequencies, lengths);

        Span<uint> codes = stackalloc uint[SymbolCount];
        AssignCanonicalCodes(lengths, codes);

        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)source.Length);

        for (int i = 0; i < LengthTableSize; i++)
            destination[sizeof(uint) + i] = (byte)(lengths[2 * i] | lengths[2 * i + 1] << 4);

        var writer = new BitWriter(destination[HeaderSize..]);

        foreach (byte value in source)
            writer.WriteBits(codes[value], lengths[value]);

        int payload = writer.Flush();
        return payload < 0 ? -1 : HeaderSize + payload;
    }

    /// <summary>
    /// Decodes through a two-level lookup table: one index per symbol for codes up to
    /// 10 bits, two for the longer and much rarer ones.
    /// </summary>
    public int Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty)
            return 0;

        Span<byte> lengths = stackalloc byte[SymbolCount];
        int count = ReadHeader(source, destination.Length, lengths);

        Span<uint> table = stackalloc uint[DecodeTableSize];
        BuildDecodeTable(lengths, table);

        var reader = new BitReader(source[HeaderSize..]);

        for (int i = 0; i < count; i++)
        {
            uint entry = table[(int)reader.PeekBits(RootBits)];

            if ((entry & LinkFlag) != 0)
            {
                reader.SkipBits(RootBits);

                int subtable = (int)((entry & ~LinkFlag) >> EntryFieldBits);
                int subtableBits = (int)(entry & EntryFieldMask);
                entry = table[subtable + (int)reader.PeekBits(subtableBits)];
            }

            if (entry == 0)
                throw new InvalidDataException("Invalid code in Huffman stream.");

            reader.SkipBits((int)(entry & EntryFieldMask));
            destination[i] = (byte)(entry >> EntryFieldBits);
        }

        if (reader.IsOverrun)
            throw new InvalidDataException("Truncated Huffman stream.");

        return count;
    }

    /// <summary>
    /// Decodes one bit at a time by walking the code tree. Same result as
    /// <see cref="Decode"/>; kept as the benchmark baseline for the lookup table.
    /// </summary>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="InvalidDataException">The input is malformed or truncated.</exception>
    public static int DecodeTreeWalk(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty)
            return 0;

        Span<byte> lengths = stackalloc byte[SymbolCount];
        int count = ReadHeader(source, destination.Length, lengths);

        // Two child slots per internal node: 0 is absent, a positive value is a node
        // index and a negative value is the complement of a symbol.
        Span<int> children = stackalloc int[2 * SymbolCount];
        BuildTree(lengths, children);

        var reader = new BitReader(source[HeaderSize..]);

        for (int i = 0; i < count; i++)
        {
            int node = 0;

            do
            {
                node = children[2 * node + (reader.ReadBit() ? 1 : 0)];

                if (node == 0)
                    throw new InvalidDataException("Invalid code in Huffman stream.");
            }
            while (node > 0);

            destination[i] = (byte)~node;
        }

        if (reader.IsOverrun)
            throw new InvalidDataException("Truncated Huffman stream.");

        return count;
    }

    /// <summary>
    /// Computes optimal code lengths: repeatedly merges the two lightest nodes of a
    /// min-heap, then reads each leaf's depth back from the parent links.
    /// </summary>
    private static void BuildCodeLengths(ReadOnlySpan<int> frequencies, Span<byte> lengths)
    {
        lengths.Clear();

        // Node ids: 0…255 are the symbols, 256… the merged nodes in creation order,
        // so a parent always has a higher id than its children.
        Span<long> weights = stackalloc long[MaxNodes];
        Span<int> parents = stackalloc int[MaxNodes];
        Span<int> heap = stackalloc int[SymbolCount];
        int heapSize = 0;

        for (int symbol = 0; symbol < SymbolCount; symbol++)
        {
            if (frequencies[symbol] > 0)
            {
                weights[symbol] = frequencies[symbol];
                HeapPush(heap, ref heapSize, weights, symbol);
            }
        }

        if (heapSize == 1)
        {
            lengths[heap[0]] = 1;
            return;
        }

        int next = SymbolCount;

        while (heapSize > 1)
        {
            int first = HeapPop(heap, ref heapSize, weights);
            int second = HeapPop(heap, ref heapSize, weights);

            weights[next] = weights[first] + weights[second];
            parents[first] = next;
            parents[second] = next;
            HeapPush(heap, ref heapSize, weights, next);
            next++;
        }

        int root = next - 1;
        Span<int> depths = stackalloc int[MaxNodes];
        depths[root] = 0;

        for (int node = root - 1; node >= SymbolCount; node--)
            depths[node] = depths[parents[node]] + 1;

        int longest = 0;

        for (int symbol = 0; symbol < SymbolCount; symbol++)
        {
            if (frequencies[symbol] > 0)
            {
                int depth = depths[parents[symbol]] + 1;
                longest = Math.Max(longest, depth);
                lengths[symbol] = (byte)depth;
            }
        }

        if (longest > MaxCodeLength)
            LimitCodeLengths(frequencies, lengths);
    }

    /// <summary>
    /// Package-merge: computes the optimal code lengths that do not exceed
    /// <see cref="MaxCodeLength"/>. Only runs when the plain tree came out too deep.
    /// </summary>
    /// <remarks>
    /// Each level is the sorted merge of all symbols with the packages (adjacent pairs)
    /// of the level below. Taking the 2k - 2 lightest items of the top level and following
    /// the packages down gives a symbol's code length as the number of levels in which
    /// it was taken. Recording only whether an item is a package is enough to do that.
    /// </remarks>
    private static void LimitCodeLengths(ReadOnlySpan<int> frequencies, Span<byte> lengths)
    {
        // Used symbols by ascending frequency.
        Span<byte> sorted = stackalloc byte[SymbolCount];
        int symbolCount = 0;

        for (int symbol = 0; symbol < SymbolCount; symbol++)
        {
            if (frequencies[symbol] == 0)
                continue;

            int slot = symbolCount++;

            while (slot > 0 && frequencies[sorted[slot - 1]] > frequencies[symbol])
            {
                sorted[slot] = sorted[slot - 1];
                slot--;
            }

            sorted[slot] = (byte)symbol;
        }

        Span<bool> isPackage = stackalloc bool[MaxCodeLength * MaxLevelItems];
        Span<long> previous = stackalloc long[MaxLevelItems];
        Span<long> current = stackalloc long[MaxLevelItems];
        int previousCount = 0;

        for (int level = MaxCodeLength - 1; level >= 0; level--)
        {
            Span<bool> flags = isPackage.Slice(level * MaxLevelItems, MaxLevelItems);
            int packageCount = previousCount / 2;
            int leaf = 0;
            int package = 0;
            int count = 0;

            while (leaf < symbolCount || package < packageCount)
            {
                long leafWeight = leaf < symbolCount ? frequencies[sorted[leaf]] : long.MaxValue;
                long packageWeight = package < packageCount
                    ? previous[2 * package] + previous[2 * package + 1]
                    : long.MaxValue;

                if (leafWeight <= packageWeight)
                {
                    current[count] = leafWeight;
                    flags[count++] = false;
                    leaf++;
                }
                else
                {
                    current[count] = packageWeight;
                    flags[count++] = true;
                    package++;
                }
            }

            Span<long> swap = previous;
            previous = current;
            current = swap;
            previousCount = count;
        }

        lengths.Clear();
        int take = 2 * symbolCount - 2;

        for (int level = 0; level < MaxCodeLength; level++)
        {
            ReadOnlySpan<bool> flags = isPackage.Slice(level * MaxLevelItems, MaxLevelItems);
            int packages = 0;
            int leaves = 0;

            for (int i = 0; i < take; i++)
            {
                if (flags[i])
                    packages++;
                else
                    lengths[sorted[leaves++]]++;
            }

            take = 2 * packages;
        }
    }

    /// <summary>
    /// Assigns each used symbol its canonical code, bit-reversed so that writing it
    /// least-significant-bit-first puts the code's first bit on the wire first.
    /// </summary>
    private static void AssignCanonicalCodes(ReadOnlySpan<byte> lengths, Span<uint> codes)
    {
        Span<int> lengthCounts = stackalloc int[MaxCodeLength + 1];
        lengthCounts.Clear();

        foreach (byte length in lengths)
            lengthCounts[length]++;

        lengthCounts[0] = 0;

        Span<uint> nextCode = stackalloc uint[MaxCodeLength + 1];
        uint code = 0;

        for (int length = 1; length <= MaxCodeLength; length++)
        {
            code = (code + (uint)lengthCounts[length - 1]) << 1;
            nextCode[length] = code;
        }

        for (int symbol = 0; symbol < SymbolCount; symbol++)
        {
            int length = lengths[symbol];
            codes[symbol] = length == 0 ? 0 : ReverseBits(nextCode[length]++, length);
        }
    }

    private static uint ReverseBits(uint value, int count)
    {
        uint reversed = 0;

        for (int i = 0; i < count; i++)
        {
            reversed = reversed << 1 | value & 1;
            value >>= 1;
        }

        return reversed;
    }

    /// <summary>
    /// Validates the header and returns the symbol count. Everything here comes from
    /// untrusted input, and the table builders rely on the lengths being a valid code.
    /// </summary>
    private static int ReadHeader(ReadOnlySpan<byte> source, int destinationLength, Span<byte> lengths)
    {
        if (source.Length < HeaderSize)
            throw new InvalidDataException("Truncated Huffman header.");

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(source);

        if (count == 0)
            throw new InvalidDataException("Huffman stream declares no symbols.");
        if (count > (uint)destinationLength)
            throw new InvalidDataException("Huffman stream expands past the declared block size.");

        // Every symbol takes at least one bit.
        if (count > (source.Length - HeaderSize) * 8L)
            throw new InvalidDataException("Truncated Huffman stream.");

        ReadOnlySpan<byte> table = source.Slice(sizeof(uint), LengthTableSize);
        int kraftSum = 0;
        int used = 0;

        for (int i = 0; i < LengthTableSize; i++)
        {
            int low = table[i] & 0x0F;
            int high = table[i] >> 4;

            lengths[2 * i] = (byte)low;
            lengths[2 * i + 1] = (byte)high;

            if (low != 0)
            {
                used++;
                kraftSum += 1 << (MaxCodeLength - low);
            }

            if (high != 0)
            {
                used++;
                kraftSum += 1 << (MaxCodeLength - high);
            }
        }

        bool complete = kraftSum == 1 << MaxCodeLength;
        bool singleSymbol = used == 1 && kraftSum == 1 << (MaxCodeLength - 1);

        if (!complete && !singleSymbol)
            throw new InvalidDataException("Huffman code lengths do not form a valid prefix code.");

        return (int)count;
    }

    /// <summary>Fills the two-level decoding table; see the layout notes on the constants.</summary>
    private static void BuildDecodeTable(ReadOnlySpan<byte> lengths, Span<uint> table)
    {
        table.Clear();

        Span<uint> codes = stackalloc uint[SymbolCount];
        AssignCanonicalCodes(lengths, codes);

        // Long codes sharing their first 10 bits share a subtable, sized for the longest.
        Span<byte> subtableBits = stackalloc byte[RootSize];
        subtableBits.Clear();

        for (int symbol = 0; symbol < SymbolCount; symbol++)
        {
            if (lengths[symbol] > RootBits)
            {
                int prefix = (int)codes[symbol] & (RootSize - 1);
                subtableBits[prefix] = Math.Max(subtableBits[prefix], (byte)(lengths[symbol] - RootBits));
            }
        }

        int nextSubtable = RootSize;

        for (int symbol = 0; symbol < SymbolCount; symbol++)
        {
            int length = lengths[symbol];

            if (length == 0)
                continue;

            uint code = codes[symbol];

            if (length <= RootBits)
            {
                uint leaf = (uint)symbol << EntryFieldBits | (uint)length;

                // The bits after a short code can be anything, so it claims every slot
                // that starts with it.
                for (int index = (int)code; index < RootSize; index += 1 << length)
                    table[index] = leaf;
            }
            else
            {
                int prefix = (int)code & (RootSize - 1);
                int bits = subtableBits[prefix];

                if (table[prefix] == 0)
                {
                    table[prefix] = LinkFlag | (uint)nextSubtable << EntryFieldBits | (uint)bits;
                    nextSubtable += 1 << bits;
                }

                int subtable = (int)((table[prefix] & ~LinkFlag) >> EntryFieldBits);
                int remaining = length - RootBits;
                uint leaf = (uint)symbol << EntryFieldBits | (uint)remaining;

                for (int index = (int)(code >> RootBits); index < 1 << bits; index += 1 << remaining)
                    table[subtable + index] = leaf;
            }
        }
    }

    /// <summary>Builds the explicit code tree walked by <see cref="DecodeTreeWalk"/>.</summary>
    private static void BuildTree(ReadOnlySpan<byte> lengths, Span<int> children)
    {
        children.Clear();

        Span<uint> codes = stackalloc uint[SymbolCount];
        AssignCanonicalCodes(lengths, codes);

        int nodeCount = 1;

        for (int symbol = 0; symbol < SymbolCount; symbol++)
        {
            int length = lengths[symbol];

            if (length == 0)
                continue;

            uint code = codes[symbol];
            int node = 0;

            for (int bit = 0; bit < length - 1; bit++)
            {
                int slot = 2 * node + (int)(code >> bit & 1);

                if (children[slot] == 0)
                    children[slot] = nodeCount++;

                node = children[slot];
            }

            children[2 * node + (int)(code >> (length - 1) & 1)] = ~symbol;
        }
    }

    /// <summary>Heap order: lighter first, ties by node id so the output is deterministic.</summary>
    private static bool Lighter(ReadOnlySpan<long> weights, int a, int b) =>
        weights[a] < weights[b] || (weights[a] == weights[b] && a < b);

    private static void HeapPush(Span<int> heap, ref int size, ReadOnlySpan<long> weights, int node)
    {
        int index = size++;

        while (index > 0)
        {
            int parent = (index - 1) / 2;

            if (!Lighter(weights, node, heap[parent]))
                break;

            heap[index] = heap[parent];
            index = parent;
        }

        heap[index] = node;
    }

    private static int HeapPop(Span<int> heap, ref int size, ReadOnlySpan<long> weights)
    {
        int top = heap[0];
        int last = heap[--size];
        int index = 0;

        while (2 * index + 1 < size)
        {
            int child = 2 * index + 1;

            if (child + 1 < size && Lighter(weights, heap[child + 1], heap[child]))
                child++;

            if (!Lighter(weights, heap[child], last))
                break;

            heap[index] = heap[child];
            index = child;
        }

        heap[index] = last;
        return top;
    }
}
