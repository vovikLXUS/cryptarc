using System.Runtime.CompilerServices;

namespace CryptArc.Core.Sarc;

/// <summary>
/// Writes bits most-significant-bit-first, as FORMAT_SPEC_P2 §0 requires: the first bit
/// written becomes bit <c>0x80</c> of the first byte, and the last byte is padded with zeros.
/// </summary>
/// <remarks>
/// The rest of the project is LSB-first (see <c>IO/AGENTS.md</c>), which is why SARC has
/// its own pair of types instead of a mode switch on <see cref="IO.BitWriter"/>.
/// The destination must be large enough; an overrun throws rather than being reported,
/// because the encoder sizes the buffer from a proven upper bound.
/// </remarks>
internal ref struct MsbBitWriter(Span<byte> destination)
{
    private readonly Span<byte> _destination = destination;
    private ulong _pending;
    private int _pendingBits;
    private int _position;

    /// <summary>Appends the low <paramref name="count"/> bits of <paramref name="value"/>, highest first.</summary>
    /// <param name="value">The bits to write.</param>
    /// <param name="count">How many low bits of <paramref name="value"/> to write, 0 to 32.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(uint value, int count)
    {
        _pending = _pending << count | (value & ((1UL << count) - 1));
        _pendingBits += count;

        while (_pendingBits >= 8)
        {
            _pendingBits -= 8;
            _destination[_position++] = (byte)(_pending >> _pendingBits);
        }
    }

    /// <summary>Writes out the last partial byte, padded with zero bits.</summary>
    /// <returns>The number of bytes written in total.</returns>
    public int Flush()
    {
        if (_pendingBits > 0)
        {
            _destination[_position++] = (byte)(_pending << (8 - _pendingBits));
            _pendingBits = 0;
        }

        return _position;
    }
}

/// <summary>
/// Reads bits most-significant-bit-first. Running out of input is reported through the
/// return value, because the caller decides which failure code that is.
/// </summary>
internal ref struct MsbBitReader(ReadOnlySpan<byte> source)
{
    private readonly ReadOnlySpan<byte> _source = source;
    private int _byte;
    private int _bit;

    /// <summary>Reads one bit.</summary>
    /// <param name="bit">The bit, 0 or 1.</param>
    /// <returns><see langword="false"/> when the input is exhausted.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryReadBit(out int bit)
    {
        if (_byte >= _source.Length)
        {
            bit = 0;
            return false;
        }

        bit = _source[_byte] >> (7 - _bit) & 1;
        if (++_bit == 8)
        {
            _bit = 0;
            _byte++;
        }

        return true;
    }

    /// <summary>Reads <paramref name="count"/> bits as an unsigned number, highest bit first.</summary>
    /// <param name="count">How many bits, 0 to 32.</param>
    /// <param name="value">The number read.</param>
    /// <returns><see langword="false"/> when the input is exhausted.</returns>
    public bool TryRead(int count, out uint value)
    {
        value = 0;
        for (int i = 0; i < count; i++)
        {
            if (!TryReadBit(out int bit))
                return false;

            value = value << 1 | (uint)bit;
        }

        return true;
    }

    /// <summary>Bytes touched so far, counting a partly read byte as a whole one.</summary>
    public readonly int BytesConsumed => _byte + (_bit > 0 ? 1 : 0);

    /// <summary>Whether the unread bits of the current byte are all zero, as the padding must be.</summary>
    public readonly bool PaddingIsZero =>
        _bit == 0 || (_source[_byte] & ((1 << (8 - _bit)) - 1)) == 0;
}
