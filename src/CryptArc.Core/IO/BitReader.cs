using System.Runtime.CompilerServices;

namespace CryptArc.Core.IO;

/// <summary>
/// Counterpart to <see cref="BitWriter"/>: least-significant-bit-first reader.
/// </summary>
public ref struct BitReader
{
    private readonly ReadOnlySpan<byte> _source;
    private ulong _accumulator;
    private int _bitCount;
    private int _position;

    public BitReader(ReadOnlySpan<byte> source)
    {
        _source = source;
        _accumulator = 0;
        _bitCount = 0;
        _position = 0;
    }

    /// <summary>Bytes consumed from the underlying buffer so far.</summary>
    public readonly int BytesConsumed => _position;

    /// <summary>Reads <paramref name="count"/> bits, zero extending past the end of input.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(int count)
    {
        if ((uint)count > 32)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Can read at most 32 bits at a time.");
        if (count == 0)
            return 0;

        while (_bitCount < count)
        {
            byte next = _position < _source.Length ? _source[_position++] : (byte)0;
            _accumulator |= (ulong)next << _bitCount;
            _bitCount += 8;
        }

        uint result = count == 32 ? (uint)_accumulator : (uint)(_accumulator & ((1u << count) - 1));
        _accumulator >>= count;
        _bitCount -= count;
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadBit() => ReadBits(1) != 0;
}
