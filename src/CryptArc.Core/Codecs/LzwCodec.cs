using System.Buffers;
using System.Runtime.CompilerServices;
using CryptArc.Core.IO;

namespace CryptArc.Core.Codecs;

/// <summary>
/// Variable bit-width Lempel-Ziv-Welch (LZW) block codec.
/// </summary>
/// <remarks>
/// <para>Stream format:</para>
/// <list type="bullet">
///   <item><description>
///     Bit-level stream packed least-significant-bit-first via <see cref="BitWriter"/> and <see cref="BitReader"/>.
///   </description></item>
///   <item><description>
///     Variable code width starts at 9 bits and dynamically expands up to 12 bits as dictionary entries are created.
///   </description></item>
///   <item><description>
///     Codes 0…255 are literal byte values (0x00…0xFF).
///   </description></item>
///   <item><description>
///     Code 256 is the Clear code: resets the dictionary back to 258 entries and resets code width to 9 bits.
///   </description></item>
///   <item><description>
///     Code 257 is the End-of-Stream (EOS) marker terminating the block.
///   </description></item>
///   <item><description>
///     Codes 258…4095 represent dynamically constructed multi-byte phrases.
///   </description></item>
/// </list>
/// <para>
/// When the dictionary reaches capacity (4096 entries at 12 bits), a Clear code is emitted and the dictionary
/// resets, allowing adaptation to changing data distributions over long blocks.
/// </para>
/// <para>Complexity:</para>
/// <list type="bullet">
///   <item><description>
///     Optimised encoder (<see cref="Encode"/>): O(n) amortised time, O(1) additional space using an open-addressing
///     hash table (power-of-two capacity 8192, load factor &lt;= 46.9%, Knuth multiplicative hash).
///   </description></item>
///   <item><description>
///     Naive encoder (<see cref="EncodeNaive"/>): O(n * D) time, where D &lt;= 4096 is dictionary size, using an unsorted
///     entry array and linear search. Kept side-by-side as a benchmark baseline and correctness oracle.
///   </description></item>
///   <item><description>
///     Optimised decoder (<see cref="Decode"/>): O(n) time, O(1) space with stack-allocated prefix/suffix trees.
///   </description></item>
/// </list>
/// </remarks>
public sealed class LzwCodec : ICodec
{
    private const int StartBits = 9;
    private const int MaxBits = 12;
    private const int ClearCode = 256;
    private const int EosCode = 257;
    private const int FirstCode = 258;
    private const int MaxCodes = 1 << MaxBits; // 4096

    private const int TableCapacity = 8192; // 2^13, power-of-two for fast bitwise masking
    private const int TableMask = TableCapacity - 1;
    private const uint HashMultiplier = 2654435761u; // Knuth's 32-bit golden ratio constant

    public CodecId Id => CodecId.Lzw;

    public string Name => "LZW";

    public int MaxCompressedSize(int sourceLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceLength);
        if (sourceLength == 0)
            return 0;

        long bound = (sourceLength * 13L + 7) / 8 + 32;
        return bound > int.MaxValue ? int.MaxValue : (int)bound;
    }

    public int Encode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty)
            return 0;

        BitWriter writer = new(destination);
        int[] keys = ArrayPool<int>.Shared.Rent(TableCapacity);
        ushort[] codes = ArrayPool<ushort>.Shared.Rent(TableCapacity);

        try
        {
            Span<int> keySpan = keys.AsSpan(0, TableCapacity);
            Span<ushort> codeSpan = codes.AsSpan(0, TableCapacity);
            keySpan.Fill(-1);

            int bitWidth = StartBits;
            int nextCode = FirstCode;
            int prefix = source[0];

            for (int i = 1; i < source.Length; i++)
            {
                byte nextByte = source[i];
                int key = (prefix << 8) | nextByte;
                int foundCode = TableLookup(keySpan, codeSpan, key);

                if (foundCode >= 0)
                {
                    prefix = foundCode;
                }
                else
                {
                    writer.WriteBits((uint)prefix, bitWidth);
                    if (writer.Overflowed)
                        return -1;

                    if (nextCode < MaxCodes)
                    {
                        TableInsert(keySpan, codeSpan, key, (ushort)nextCode);
                        nextCode++;

                        if (nextCode == (1 << bitWidth) + 1 && bitWidth < MaxBits)
                            bitWidth++;
                    }
                    else
                    {
                        writer.WriteBits(ClearCode, bitWidth);
                        if (writer.Overflowed)
                            return -1;

                        keySpan.Fill(-1);
                        nextCode = FirstCode;
                        bitWidth = StartBits;
                    }

                    prefix = nextByte;
                }
            }

            writer.WriteBits((uint)prefix, bitWidth);
            if (writer.Overflowed)
                return -1;

            writer.WriteBits(EosCode, bitWidth);
            if (writer.Overflowed)
                return -1;

            return writer.Flush();
        }
        finally
        {
            ArrayPool<int>.Shared.Return(keys);
            ArrayPool<ushort>.Shared.Return(codes);
        }
    }

    /// <summary>
    /// Naive LZW encoder using linear search over an array of entries.
    /// Bit-for-bit identical to <see cref="Encode"/>, preserved as the benchmark baseline.
    /// </summary>
    public int EncodeNaive(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty)
            return 0;

        BitWriter writer = new(destination);
        NaiveEntry[] table = ArrayPool<NaiveEntry>.Shared.Rent(MaxCodes);
        int tableCount = 0;

        try
        {
            int bitWidth = StartBits;
            int nextCode = FirstCode;
            int prefix = source[0];

            for (int i = 1; i < source.Length; i++)
            {
                byte nextByte = source[i];
                int key = (prefix << 8) | nextByte;
                int foundCode = -1;

                for (int t = 0; t < tableCount; t++)
                {
                    if (table[t].Key == key)
                    {
                        foundCode = table[t].Code;
                        break;
                    }
                }

                if (foundCode >= 0)
                {
                    prefix = foundCode;
                }
                else
                {
                    writer.WriteBits((uint)prefix, bitWidth);
                    if (writer.Overflowed)
                        return -1;

                    if (nextCode < MaxCodes)
                    {
                        table[tableCount++] = new NaiveEntry { Key = key, Code = (ushort)nextCode };
                        nextCode++;

                        if (nextCode == (1 << bitWidth) + 1 && bitWidth < MaxBits)
                            bitWidth++;
                    }
                    else
                    {
                        writer.WriteBits(ClearCode, bitWidth);
                        if (writer.Overflowed)
                            return -1;

                        tableCount = 0;
                        nextCode = FirstCode;
                        bitWidth = StartBits;
                    }

                    prefix = nextByte;
                }
            }

            writer.WriteBits((uint)prefix, bitWidth);
            if (writer.Overflowed)
                return -1;

            writer.WriteBits(EosCode, bitWidth);
            if (writer.Overflowed)
                return -1;

            return writer.Flush();
        }
        finally
        {
            ArrayPool<NaiveEntry>.Shared.Return(table);
        }
    }

    public int Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty)
            return 0;

        BitReader reader = new(source);
        Span<ushort> prefixes = stackalloc ushort[MaxCodes];
        Span<byte> suffixes = stackalloc byte[MaxCodes];
        Span<byte> stackBuffer = stackalloc byte[MaxCodes];

        int bitWidth = StartBits;
        int nextCode = FirstCode;
        int oldCode = -1;
        int written = 0;
        long totalBitsRead = 0;
        long totalBitsAvailable = (long)source.Length * 8;

        while (true)
        {
            if (totalBitsRead + bitWidth > totalBitsAvailable)
                throw new InvalidDataException("Truncated LZW stream: reached end of input without end-of-stream code.");

            uint code = reader.ReadBits(bitWidth);
            totalBitsRead += bitWidth;

            if (code == EosCode)
                break;

            if (code == ClearCode)
            {
                nextCode = FirstCode;
                bitWidth = StartBits;
                oldCode = -1;
                continue;
            }

            if (oldCode == -1)
            {
                if (code >= 256)
                    throw new InvalidDataException($"Invalid initial LZW code: {code}.");

                if (written >= destination.Length)
                    throw new InvalidDataException("LZW stream expands past the destination buffer.");

                destination[written++] = (byte)code;
                oldCode = (int)code;
                continue;
            }

            byte firstChar;

            if (code < nextCode)
            {
                if (code < 256)
                {
                    firstChar = (byte)code;
                    if (written >= destination.Length)
                        throw new InvalidDataException("LZW stream expands past the destination buffer.");

                    destination[written++] = firstChar;
                }
                else
                {
                    int top = MaxCodes;
                    int cur = (int)code;

                    while (cur >= 258)
                    {
                        stackBuffer[--top] = suffixes[cur];
                        cur = prefixes[cur];
                        if (top <= 0)
                            throw new InvalidDataException("Corrupted LZW dictionary cycle detected.");
                    }

                    stackBuffer[--top] = (byte)cur;
                    firstChar = (byte)cur;

                    int length = MaxCodes - top;
                    if (written + length > destination.Length)
                        throw new InvalidDataException("LZW stream expands past the destination buffer.");

                    stackBuffer.Slice(top, length).CopyTo(destination[written..]);
                    written += length;
                }
            }
            else if (code == nextCode)
            {
                int top = MaxCodes - 1;
                int cur = oldCode;

                while (cur >= 258)
                {
                    stackBuffer[--top] = suffixes[cur];
                    cur = prefixes[cur];
                    if (top <= 0)
                        throw new InvalidDataException("Corrupted LZW dictionary cycle detected.");
                }

                stackBuffer[--top] = (byte)cur;
                firstChar = (byte)cur;
                stackBuffer[MaxCodes - 1] = firstChar;

                int length = MaxCodes - top;
                if (written + length > destination.Length)
                    throw new InvalidDataException("LZW stream expands past the destination buffer.");

                stackBuffer.Slice(top, length).CopyTo(destination[written..]);
                written += length;
            }
            else
            {
                throw new InvalidDataException($"Invalid LZW code {code} (current next code is {nextCode}).");
            }

            if (nextCode < MaxCodes)
            {
                prefixes[nextCode] = (ushort)oldCode;
                suffixes[nextCode] = firstChar;
                nextCode++;

                if (nextCode == (1 << bitWidth) && bitWidth < MaxBits)
                    bitWidth++;
            }

            oldCode = (int)code;
        }

        return written;
    }

    /// <summary>
    /// Naive LZW decoder constructing explicit string arrays per dictionary entry.
    /// Preserved as the benchmark baseline and oracle.
    /// </summary>
    public int DecodeNaive(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty)
            return 0;

        BitReader reader = new(source);
        byte[][] dictionary = new byte[MaxCodes][];
        for (int i = 0; i < 256; i++)
            dictionary[i] = [(byte)i];

        int bitWidth = StartBits;
        int nextCode = FirstCode;
        int oldCode = -1;
        int written = 0;
        long totalBitsRead = 0;
        long totalBitsAvailable = (long)source.Length * 8;

        while (true)
        {
            if (totalBitsRead + bitWidth > totalBitsAvailable)
                throw new InvalidDataException("Truncated LZW stream: reached end of input without end-of-stream code.");

            uint code = reader.ReadBits(bitWidth);
            totalBitsRead += bitWidth;

            if (code == EosCode)
                break;

            if (code == ClearCode)
            {
                for (int i = FirstCode; i < nextCode; i++)
                    dictionary[i] = null!;

                nextCode = FirstCode;
                bitWidth = StartBits;
                oldCode = -1;
                continue;
            }

            if (oldCode == -1)
            {
                if (code >= 256)
                    throw new InvalidDataException($"Invalid initial LZW code: {code}.");

                if (written >= destination.Length)
                    throw new InvalidDataException("LZW stream expands past the destination buffer.");

                destination[written++] = (byte)code;
                oldCode = (int)code;
                continue;
            }

            byte[] entry;

            if (code < nextCode)
            {
                byte[]? candidate = dictionary[code];
                if (candidate is null)
                    throw new InvalidDataException($"Invalid LZW dictionary access at code {code}.");
                entry = candidate;
            }
            else if (code == nextCode)
            {
                byte[]? oldEntry = dictionary[oldCode];
                if (oldEntry is null)
                    throw new InvalidDataException($"Invalid LZW dictionary access at old code {oldCode}.");

                entry = new byte[oldEntry.Length + 1];
                oldEntry.CopyTo(entry, 0);
                entry[^1] = oldEntry[0];
            }
            else
            {
                throw new InvalidDataException($"Invalid LZW code {code} (current next code is {nextCode}).");
            }

            if (written + entry.Length > destination.Length)
                throw new InvalidDataException("LZW stream expands past the destination buffer.");

            entry.CopyTo(destination[written..]);
            written += entry.Length;

            if (nextCode < MaxCodes)
            {
                byte[]? oldEntry = dictionary[oldCode];
                if (oldEntry is null)
                    throw new InvalidDataException($"Invalid LZW dictionary access at old code {oldCode}.");

                byte[] newEntry = new byte[oldEntry.Length + 1];
                oldEntry.CopyTo(newEntry, 0);
                newEntry[^1] = entry[0];
                dictionary[nextCode] = newEntry;
                nextCode++;

                if (nextCode == (1 << bitWidth) && bitWidth < MaxBits)
                    bitWidth++;
            }

            oldCode = (int)code;
        }

        return written;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Hash(int key) =>
        (int)(((uint)key * HashMultiplier) >> (32 - 13));

    private static int TableLookup(ReadOnlySpan<int> keys, ReadOnlySpan<ushort> codes, int key)
    {
        int index = Hash(key);

        while (true)
        {
            int existing = keys[index];
            if (existing == key)
                return codes[index];
            if (existing == -1)
                return -1;

            index = (index + 1) & TableMask;
        }
    }

    private static void TableInsert(Span<int> keys, Span<ushort> codes, int key, ushort code)
    {
        int index = Hash(key);

        while (keys[index] != -1)
        {
            index = (index + 1) & TableMask;
        }

        keys[index] = key;
        codes[index] = code;
    }

    private struct NaiveEntry
    {
        public int Key;
        public ushort Code;
    }
}
