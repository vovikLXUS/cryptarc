using System.Buffers.Binary;
using CryptArc.Core.Coding;

namespace CryptArc.Core.Sarc;

/// <summary>
/// The code tables of FORMAT_SPEC_P2 §3.3–3.4: serialisation, validation, the canonical
/// code assignment and a decoder.
/// </summary>
/// <remarks>
/// <para>Serialised table, the same for all three trees:</para>
/// <code>
/// nsym     uint16 BE
/// nsym × { symbol uint16 BE, length uint8 (1…15) }   in ascending symbol order
/// </code>
/// <para>
/// Code lengths come from <see cref="CanonicalHuffman.BuildLengths"/>, which falls back to
/// package-merge when a plain Huffman tree would be deeper than 15 bits. The codes are
/// assigned here rather than by <see cref="CanonicalHuffman.AssignCodes"/> because that one
/// bit-reverses them for the LSB-first streams; SARC writes them as they are.
/// </para>
/// </remarks>
internal static class SarcCodeTable
{
    /// <summary>The longest code the format permits.</summary>
    public const int MaxCodeLength = 15;

    private const int EntrySize = 3;

    /// <summary>Optimal code lengths no longer than <see cref="MaxCodeLength"/>; a lone symbol gets length 1.</summary>
    /// <param name="frequencies">Symbol frequencies; its length is the alphabet size.</param>
    /// <param name="lengths">Receives one code length per symbol, 0 for an unused one.</param>
    public static void BuildLengths(ReadOnlySpan<int> frequencies, Span<byte> lengths) =>
        CanonicalHuffman.BuildLengths(frequencies, lengths, MaxCodeLength);

    /// <summary>Bytes <see cref="Write"/> produces for these lengths.</summary>
    /// <param name="lengths">One code length per symbol.</param>
    /// <returns>The serialised size.</returns>
    public static int SerializedSize(ReadOnlySpan<byte> lengths)
    {
        int used = 0;
        foreach (byte length in lengths)
        {
            if (length != 0)
                used++;
        }

        return sizeof(ushort) + used * EntrySize;
    }

    /// <summary>Serialises a table.</summary>
    /// <param name="lengths">One code length per symbol; its length is the alphabet size.</param>
    /// <param name="destination">Receives the table; must hold <see cref="SerializedSize"/> bytes.</param>
    /// <returns>The number of bytes written.</returns>
    public static int Write(ReadOnlySpan<byte> lengths, Span<byte> destination)
    {
        int position = sizeof(ushort);
        for (int symbol = 0; symbol < lengths.Length; symbol++)
        {
            if (lengths[symbol] == 0)
                continue;

            BinaryPrimitives.WriteUInt16BigEndian(destination[position..], (ushort)symbol);
            destination[position + 2] = lengths[symbol];
            position += EntrySize;
        }

        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)((position - sizeof(ushort)) / EntrySize));
        return position;
    }

    /// <summary>Parses and validates a table.</summary>
    /// <param name="source">The bytes from the start of the table to the end of the block.</param>
    /// <param name="lengths">Cleared, then receives one code length per symbol; its length is the alphabet size.</param>
    /// <param name="name">The tree's name for messages: LIT, LEN or DIST.</param>
    /// <returns>The number of bytes the table occupies.</returns>
    /// <exception cref="SarcException">
    /// <see cref="SarcErrorCode.Payload"/> when the table runs past the block;
    /// <see cref="SarcErrorCode.Code"/> for a symbol outside the alphabet, symbols out of
    /// order, a length of 0 or above 15, a lone symbol whose length is not 1, or a set of
    /// lengths that does not satisfy the Kraft equality exactly.
    /// </exception>
    public static int Read(ReadOnlySpan<byte> source, Span<byte> lengths, string name)
    {
        if (source.Length < sizeof(ushort))
            throw new SarcException(SarcErrorCode.Payload, $"The block ends before the {name} table.");

        int count = BinaryPrimitives.ReadUInt16BigEndian(source);
        int size = sizeof(ushort) + count * EntrySize;
        if (source.Length < size)
            throw new SarcException(SarcErrorCode.Payload, $"The {name} table runs past the end of the block.");

        if (count > lengths.Length)
            throw new SarcException(SarcErrorCode.Code, $"The {name} table lists {count} symbols; its alphabet has {lengths.Length}.");

        lengths.Clear();
        int previous = -1;
        long kraft = 0;

        for (int entry = 0; entry < count; entry++)
        {
            ReadOnlySpan<byte> record = source.Slice(sizeof(ushort) + entry * EntrySize, EntrySize);
            int symbol = BinaryPrimitives.ReadUInt16BigEndian(record);
            int length = record[2];

            if (symbol >= lengths.Length)
                throw new SarcException(SarcErrorCode.Code, $"Symbol {symbol} is outside the {name} alphabet 0…{lengths.Length - 1}.");

            if (symbol <= previous)
                throw new SarcException(SarcErrorCode.Code, $"The {name} table is not in ascending symbol order at symbol {symbol}.");

            if (length is 0 or > MaxCodeLength)
                throw new SarcException(SarcErrorCode.Code, $"Symbol {symbol} of the {name} table has length {length}; 1…{MaxCodeLength} is allowed.");

            lengths[symbol] = (byte)length;
            kraft += 1L << (MaxCodeLength - length);
            previous = symbol;
        }

        if (count == 1 && lengths[previous] != 1)
            throw new SarcException(SarcErrorCode.Code, $"The lone symbol of the {name} table must have length 1.");

        if (count > 1 && kraft != 1L << MaxCodeLength)
            throw new SarcException(SarcErrorCode.Code, $"The {name} code lengths break the Kraft equality.");

        return size;
    }

    /// <summary>
    /// Assigns canonical codes exactly as FORMAT_SPEC_P2 §3.4 prescribes, most significant
    /// bit first and not reversed.
    /// </summary>
    /// <param name="lengths">One code length per symbol.</param>
    /// <param name="codes">Receives one code per symbol, 0 for an unused one.</param>
    public static void AssignCodes(ReadOnlySpan<byte> lengths, Span<uint> codes)
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

        for (int symbol = 0; symbol < lengths.Length; symbol++)
        {
            int length = lengths[symbol];
            codes[symbol] = length == 0 ? 0 : nextCode[length]++;
        }
    }
}

/// <summary>
/// Decodes one symbol at a time from canonical code lengths: for each length it knows how
/// many codes there are and which symbols they belong to, so no tree is built.
/// </summary>
/// <remarks>
/// Each symbol costs at most 15 single-bit reads. That is the straightforward decoder; a
/// lookup table would be faster, and would have to keep this one as its baseline.
/// </remarks>
internal readonly ref struct SarcSymbolDecoder
{
    /// <summary>Returned by <see cref="Decode"/> when the bits form no code of the table.</summary>
    public const int InvalidCode = -1;

    /// <summary>Returned by <see cref="Decode"/> when the stream ends inside a code.</summary>
    public const int EndOfStream = -2;

    private readonly ReadOnlySpan<int> _counts;
    private readonly ReadOnlySpan<ushort> _symbols;

    /// <summary>Prepares a decoder.</summary>
    /// <param name="lengths">One validated code length per symbol.</param>
    /// <param name="counts">Scratch for the per-length code counts; 16 entries.</param>
    /// <param name="symbols">Scratch for the symbols in canonical order; one entry per symbol of the alphabet.</param>
    public SarcSymbolDecoder(ReadOnlySpan<byte> lengths, Span<int> counts, Span<ushort> symbols)
    {
        counts.Clear();
        foreach (byte length in lengths)
            counts[length]++;

        counts[0] = 0;

        Span<int> offsets = stackalloc int[SarcCodeTable.MaxCodeLength + 2];
        offsets.Clear();
        for (int length = 1; length <= SarcCodeTable.MaxCodeLength; length++)
            offsets[length + 1] = offsets[length] + counts[length];

        for (int symbol = 0; symbol < lengths.Length; symbol++)
        {
            if (lengths[symbol] != 0)
                symbols[offsets[lengths[symbol]]++] = (ushort)symbol;
        }

        _counts = counts;
        _symbols = symbols;
    }

    /// <summary>Reads one code and returns its symbol.</summary>
    /// <param name="reader">The bit stream.</param>
    /// <returns>The symbol, <see cref="InvalidCode"/> or <see cref="EndOfStream"/>.</returns>
    public int Decode(scoped ref MsbBitReader reader)
    {
        int code = 0;
        int first = 0;
        int index = 0;

        for (int length = 1; length <= SarcCodeTable.MaxCodeLength; length++)
        {
            if (!reader.TryReadBit(out int bit))
                return EndOfStream;

            code |= bit;
            int count = _counts[length];
            if (code - first < count)
                return _symbols[index + code - first];

            index += count;
            first = (first + count) << 1;
            code <<= 1;
        }

        return InvalidCode;
    }
}
