namespace CryptArc.Core.Coding;

/// <summary>
/// Canonical Huffman code construction, shared by every entropy coder in the project:
/// optimal code lengths, length limiting, canonical code assignment, and the two
/// decoding structures (a two-level lookup table and an explicit tree).
/// </summary>
/// <remarks>
/// <para>
/// The alphabet size is taken from the span lengths and the longest permitted code is a
/// parameter, because the codecs need different shapes. <see cref="Codecs.HuffmanCodec"/>
/// codes 256 byte values with codes of up to 15 bits; Deflate needs 288 literal/length
/// symbols, 30 distance symbols, and a third 19-symbol alphabet limited to 7 bits.
/// </para>
/// <para>
/// A canonical code is defined most-significant-bit-first, while the bit streams in this
/// project are least-significant-bit-first. <see cref="AssignCodes"/> therefore returns
/// every code already bit-reversed: writing it low bit first puts the code's first bit on
/// the wire first.
/// </para>
/// <para>
/// Nothing here allocates. Scratch buffers are <c>stackalloc</c> sized from the alphabet,
/// and every output buffer belongs to the caller.
/// </para>
/// <para>
/// Complexity, with k symbols and L = <c>maxCodeLength</c>:
/// <see cref="BuildLengths"/> is O(k log k), plus O(k·L) on the rare alphabets that need
/// limiting; <see cref="AssignCodes"/> and <see cref="BuildDecodeTable"/> are O(k + 2^L).
/// </para>
/// </remarks>
internal static class CanonicalHuffman
{
    /// <summary>
    /// The longest code this type can build. The decode-table entry spends
    /// <see cref="EntryFieldBits"/> bits on "how many bits to consume", which caps it at 15.
    /// </summary>
    internal const int MaxSupportedCodeLength = 15;

    /// <summary>Set on a decode-table entry that links to a subtable instead of naming a symbol.</summary>
    internal const uint LinkFlag = 0x8000_0000;

    /// <summary>Width of the low field of a decode-table entry: bits to consume, or subtable index bits.</summary>
    internal const int EntryFieldBits = 4;

    /// <summary>Mask for the low field of a decode-table entry.</summary>
    internal const uint EntryFieldMask = (1u << EntryFieldBits) - 1;

    /// <summary>
    /// Computes optimal code lengths: repeatedly merges the two lightest nodes of a
    /// min-heap, then reads each leaf's depth back from the parent links. Falls back to
    /// package-merge when the resulting tree is deeper than <paramref name="maxCodeLength"/>.
    /// </summary>
    /// <param name="frequencies">Symbol frequencies; its length is the alphabet size.</param>
    /// <param name="lengths">Receives one code length per symbol; 0 means the symbol does not occur.</param>
    /// <param name="maxCodeLength">The longest code permitted, at most <see cref="MaxSupportedCodeLength"/>.</param>
    /// <remarks>
    /// A single distinct symbol gets the 1-bit code <c>0</c>: a tree with one leaf has depth
    /// 0, which could not encode anything.
    /// </remarks>
    internal static void BuildLengths(ReadOnlySpan<int> frequencies, Span<byte> lengths, int maxCodeLength)
    {
        int alphabet = frequencies.Length;
        lengths.Clear();

        // Node ids: 0…alphabet-1 are the symbols, alphabet… the merged nodes in creation
        // order, so a parent always has a higher id than its children.
        int maxNodes = 2 * alphabet - 1;
        Span<long> weights = stackalloc long[maxNodes];
        Span<int> parents = stackalloc int[maxNodes];
        Span<int> heap = stackalloc int[alphabet];
        int heapSize = 0;

        for (int symbol = 0; symbol < alphabet; symbol++)
        {
            if (frequencies[symbol] > 0)
            {
                weights[symbol] = frequencies[symbol];
                HeapPush(heap, ref heapSize, weights, symbol);
            }
        }

        if (heapSize == 0)
            return;

        if (heapSize == 1)
        {
            lengths[heap[0]] = 1;
            return;
        }

        int next = alphabet;

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
        Span<int> depths = stackalloc int[maxNodes];
        depths[root] = 0;

        for (int node = root - 1; node >= alphabet; node--)
            depths[node] = depths[parents[node]] + 1;

        int longest = 0;

        for (int symbol = 0; symbol < alphabet; symbol++)
        {
            if (frequencies[symbol] > 0)
            {
                int depth = depths[parents[symbol]] + 1;
                longest = Math.Max(longest, depth);
                lengths[symbol] = (byte)depth;
            }
        }

        if (longest > maxCodeLength)
            LimitLengths(frequencies, lengths, maxCodeLength);
    }

    /// <summary>
    /// Package-merge: computes the optimal code lengths that do not exceed
    /// <paramref name="maxCodeLength"/>. Only worth running when the plain tree came out
    /// too deep, which is what <see cref="BuildLengths"/> does.
    /// </summary>
    /// <param name="frequencies">Symbol frequencies; its length is the alphabet size.</param>
    /// <param name="lengths">Receives one code length per symbol; 0 means the symbol does not occur.</param>
    /// <param name="maxCodeLength">The longest code permitted.</param>
    /// <remarks>
    /// Each level is the sorted merge of all symbols with the packages (adjacent pairs) of
    /// the level below. Taking the 2k - 2 lightest items of the top level and following the
    /// packages down gives a symbol's code length as the number of levels in which it was
    /// taken. Recording only whether an item is a package is enough to do that.
    /// </remarks>
    internal static void LimitLengths(ReadOnlySpan<int> frequencies, Span<byte> lengths, int maxCodeLength)
    {
        int alphabet = frequencies.Length;

        // Used symbols by ascending frequency: heap sort, O(k log k).
        Span<long> weights = stackalloc long[alphabet];
        Span<int> heap = stackalloc int[alphabet];
        int heapSize = 0;

        for (int symbol = 0; symbol < alphabet; symbol++)
        {
            if (frequencies[symbol] > 0)
            {
                weights[symbol] = frequencies[symbol];
                HeapPush(heap, ref heapSize, weights, symbol);
            }
        }

        int symbolCount = heapSize;

        // ushort, not byte: an alphabet can hold more than 256 symbols (Deflate has 288).
        Span<ushort> sorted = stackalloc ushort[alphabet];

        for (int i = 0; i < symbolCount; i++)
            sorted[i] = (ushort)HeapPop(heap, ref heapSize, weights);

        // A level merges at most k leaves with fewer than k packages.
        int maxLevelItems = 2 * alphabet;
        Span<bool> isPackage = stackalloc bool[maxCodeLength * maxLevelItems];
        Span<long> previous = stackalloc long[maxLevelItems];
        Span<long> current = stackalloc long[maxLevelItems];
        int previousCount = 0;

        for (int level = maxCodeLength - 1; level >= 0; level--)
        {
            Span<bool> levelIsPackage = isPackage.Slice(level * maxLevelItems, maxLevelItems);
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
                    levelIsPackage[count++] = false;
                    leaf++;
                }
                else
                {
                    current[count] = packageWeight;
                    levelIsPackage[count++] = true;
                    package++;
                }
            }

            Span<long> swap = previous;
            previous = current;
            current = swap;
            previousCount = count;
        }

        lengths.Clear();
        int selectedItems = 2 * symbolCount - 2;

        for (int level = 0; level < maxCodeLength; level++)
        {
            ReadOnlySpan<bool> levelIsPackage = isPackage.Slice(level * maxLevelItems, maxLevelItems);
            int packages = 0;
            int leaves = 0;

            for (int i = 0; i < selectedItems; i++)
            {
                if (levelIsPackage[i])
                    packages++;
                else
                    lengths[sorted[leaves++]]++;
            }

            selectedItems = 2 * packages;
        }
    }

    /// <summary>
    /// Assigns each used symbol its canonical code, bit-reversed so that writing it
    /// least-significant-bit-first puts the code's first bit on the wire first.
    /// </summary>
    /// <param name="lengths">One code length per symbol; its length is the alphabet size.</param>
    /// <param name="codes">Receives one reversed code per symbol; 0 for unused symbols.</param>
    /// <param name="maxCodeLength">The longest code present in <paramref name="lengths"/>.</param>
    internal static void AssignCodes(ReadOnlySpan<byte> lengths, Span<uint> codes, int maxCodeLength)
    {
        Span<int> lengthCounts = stackalloc int[maxCodeLength + 1];
        lengthCounts.Clear();

        foreach (byte length in lengths)
            lengthCounts[length]++;

        lengthCounts[0] = 0;

        Span<uint> nextCode = stackalloc uint[maxCodeLength + 1];
        uint code = 0;

        for (int length = 1; length <= maxCodeLength; length++)
        {
            code = (code + (uint)lengthCounts[length - 1]) << 1;
            nextCode[length] = code;
        }

        for (int symbol = 0; symbol < lengths.Length; symbol++)
        {
            int length = lengths[symbol];
            codes[symbol] = length == 0 ? 0 : ReverseBits(nextCode[length]++, length);
        }
    }

    /// <summary>
    /// How many entries <see cref="BuildDecodeTable"/> needs for the given shape: the root
    /// table, plus room for the subtables that the codes longer than
    /// <paramref name="rootBits"/> require.
    /// </summary>
    /// <param name="alphabetSize">Number of symbols in the alphabet.</param>
    /// <param name="maxCodeLength">The longest code permitted.</param>
    /// <param name="rootBits">Bits consumed by one lookup in the root table.</param>
    /// <returns>The required length of the <c>table</c> span.</returns>
    /// <remarks>
    /// A subtable covers at most <c>maxCodeLength - rootBits</c> bits and, the code being
    /// complete, holds at least two leaves, so there are at most <c>alphabetSize / 2</c> of them.
    /// </remarks>
    internal static int DecodeTableSize(int alphabetSize, int maxCodeLength, int rootBits) =>
        maxCodeLength <= rootBits
            ? 1 << rootBits
            : (1 << rootBits) + (alphabetSize / 2 << (maxCodeLength - rootBits));

    /// <summary>
    /// Fills a two-level decoding table: one lookup per symbol for codes up to
    /// <paramref name="rootBits"/> bits, two for the longer and much rarer ones.
    /// </summary>
    /// <param name="lengths">One code length per symbol; its length is the alphabet size.</param>
    /// <param name="table">
    /// Receives the table; size it with <see cref="DecodeTableSize"/>.
    /// An entry of 0 is invalid, a leaf is <c>(symbol &lt;&lt; EntryFieldBits) | bitsToConsume</c>,
    /// and a link is <c>LinkFlag | (subtableOffset &lt;&lt; EntryFieldBits) | subtableIndexBits</c>.
    /// </param>
    /// <param name="maxCodeLength">The longest code permitted.</param>
    /// <param name="rootBits">Bits consumed by one lookup in the root table.</param>
    internal static void BuildDecodeTable(
        ReadOnlySpan<byte> lengths, Span<uint> table, int maxCodeLength, int rootBits)
    {
        table.Clear();

        int alphabet = lengths.Length;
        int rootSize = 1 << rootBits;

        Span<uint> codes = stackalloc uint[alphabet];
        AssignCodes(lengths, codes, maxCodeLength);

        // Long codes sharing their first rootBits bits share a subtable, sized for the longest.
        Span<byte> subtableBits = stackalloc byte[rootSize];
        subtableBits.Clear();

        for (int symbol = 0; symbol < alphabet; symbol++)
        {
            if (lengths[symbol] > rootBits)
            {
                int prefix = (int)codes[symbol] & (rootSize - 1);
                subtableBits[prefix] = Math.Max(subtableBits[prefix], (byte)(lengths[symbol] - rootBits));
            }
        }

        int nextSubtable = rootSize;

        for (int symbol = 0; symbol < alphabet; symbol++)
        {
            int length = lengths[symbol];

            if (length == 0)
                continue;

            uint code = codes[symbol];

            if (length <= rootBits)
            {
                uint leaf = (uint)symbol << EntryFieldBits | (uint)length;

                // The bits after a short code can be anything, so it claims every slot
                // that starts with it.
                for (int index = (int)code; index < rootSize; index += 1 << length)
                    table[index] = leaf;
            }
            else
            {
                int prefix = (int)code & (rootSize - 1);
                int bits = subtableBits[prefix];

                if (table[prefix] == 0)
                {
                    table[prefix] = LinkFlag | (uint)nextSubtable << EntryFieldBits | (uint)bits;
                    nextSubtable += 1 << bits;
                }

                int subtable = (int)((table[prefix] & ~LinkFlag) >> EntryFieldBits);
                int remaining = length - rootBits;
                uint leaf = (uint)symbol << EntryFieldBits | (uint)remaining;

                for (int index = (int)(code >> rootBits); index < 1 << bits; index += 1 << remaining)
                    table[subtable + index] = leaf;
            }
        }
    }

    /// <summary>Builds the explicit code tree walked by the naive decoders.</summary>
    /// <param name="lengths">One code length per symbol; its length is the alphabet size.</param>
    /// <param name="children">
    /// Receives two child slots per internal node, so it must hold <c>2 * alphabetSize</c>
    /// entries. 0 is absent, a positive value is a node index, and a negative value is the
    /// complement of a symbol.
    /// </param>
    /// <param name="maxCodeLength">The longest code permitted.</param>
    internal static void BuildTree(ReadOnlySpan<byte> lengths, Span<int> children, int maxCodeLength)
    {
        children.Clear();

        int alphabet = lengths.Length;
        Span<uint> codes = stackalloc uint[alphabet];
        AssignCodes(lengths, codes, maxCodeLength);

        int nodeCount = 1;

        for (int symbol = 0; symbol < alphabet; symbol++)
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

    /// <summary>Reverses the low <paramref name="count"/> bits of <paramref name="value"/>.</summary>
    /// <param name="value">The value to reverse.</param>
    /// <param name="count">How many low bits take part.</param>
    /// <returns>The reversed bits, in the low <paramref name="count"/> positions.</returns>
    internal static uint ReverseBits(uint value, int count)
    {
        uint reversed = 0;

        for (int i = 0; i < count; i++)
        {
            reversed = reversed << 1 | value & 1;
            value >>= 1;
        }

        return reversed;
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
