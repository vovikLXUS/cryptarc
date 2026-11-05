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
    private long _paddingBytes;

    /// <summary>Initializes a new instance of the <see cref="BitReader"/> struct over <paramref name="source"/>.</summary>
    /// <param name="source">The bytes to read bits from.</param>
    public BitReader(ReadOnlySpan<byte> source)
    {
        _source = source;
        _accumulator = 0;
        _bitCount = 0;
        _position = 0;
        _paddingBytes = 0;
    }

    /// <summary>Bytes consumed from the underlying buffer so far.</summary>
    public readonly int BytesConsumed => _position;

    /// <summary>
    /// True once more bits were consumed than the input holds, meaning some of them
    /// were the implicit zero padding. Peeking past the end does not set this; only
    /// consuming does, so a decoder can look ahead freely near the end of a stream.
    /// </summary>
    public readonly bool IsOverrun => _paddingBytes * 8 > _bitCount;

    /// <summary>Reads <paramref name="count"/> bits, zero extending past the end of input.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(int count)
    {
        uint result = PeekBits(count);
        _accumulator >>= count;
        _bitCount -= count;
        return result;
    }

    /// <summary>
    /// Returns the next <paramref name="count"/> bits without consuming them, zero
    /// extending past the end of input.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint PeekBits(int count)
    {
        Fill(count);
        return count == 32 ? (uint)_accumulator : (uint)(_accumulator & ((1u << count) - 1));
    }

    /// <summary>Consumes <paramref name="count"/> bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipBits(int count)
    {
        Fill(count);
        _accumulator >>= count;
        _bitCount -= count;
    }

    /// <summary>Reads one bit, zero extending past the end of input.</summary>
    /// <returns><see langword="true"/> if the bit is 1; otherwise, <see langword="false"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadBit() => ReadBits(1) != 0;

    /// <summary>Tops the accumulator up until it holds at least <paramref name="count"/> bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Fill(int count)
    {
        if ((uint)count > 32)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Can read at most 32 bits at a time.");

        while (_bitCount < count)
        {
            byte next = 0;

            if (_position < _source.Length)
                next = _source[_position++];
            else
                _paddingBytes++;

            _accumulator |= (ulong)next << _bitCount;
            _bitCount += 8;
        }
    }
}
