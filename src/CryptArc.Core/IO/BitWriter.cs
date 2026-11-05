using System.Runtime.CompilerServices;

namespace CryptArc.Core.IO;

/// <summary>
/// Least-significant-bit-first bit writer over a caller supplied buffer.
/// </summary>
/// <remarks>
/// Bits are staged in a 64-bit accumulator and flushed a whole byte at a time.
/// The naive alternative — one branch per bit — is roughly an order of magnitude
/// slower; both variants are kept so the difference can be measured
/// (see <c>bench/CryptArc.Bench</c>).
/// </remarks>
public ref struct BitWriter
{
    private readonly Span<byte> _destination;
    private ulong _accumulator;
    private int _bitCount;
    private int _position;
    private bool _overflowed;

    /// <summary>Initializes a new instance of the <see cref="BitWriter"/> struct over <paramref name="destination"/>.</summary>
    /// <param name="destination">The buffer that receives the bytes.</param>
    public BitWriter(Span<byte> destination)
    {
        _destination = destination;
        _accumulator = 0;
        _bitCount = 0;
        _position = 0;
        _overflowed = false;
    }

    /// <summary>True once a write did not fit; every later write is discarded.</summary>
    public readonly bool Overflowed => _overflowed;

    /// <summary>Whole bytes written to the destination so far, not counting bits still in the accumulator.</summary>
    public readonly int BytesWritten => _position;

    /// <summary>Writes the low <paramref name="count"/> bits of <paramref name="value"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBits(uint value, int count)
    {
        if ((uint)count > 32)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Can write at most 32 bits at a time.");
        if (_overflowed || count == 0)
            return;

        ulong masked = count == 32 ? value : value & ((1u << count) - 1);
        _accumulator |= masked << _bitCount;
        _bitCount += count;

        while (_bitCount >= 8)
        {
            if (_position >= _destination.Length)
            {
                _overflowed = true;
                return;
            }

            _destination[_position++] = (byte)_accumulator;
            _accumulator >>= 8;
            _bitCount -= 8;
        }
    }

    /// <summary>Writes one bit.</summary>
    /// <param name="bit"><see langword="true"/> to write 1; otherwise, <see langword="false"/> to write 0.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBit(bool bit) => WriteBits(bit ? 1u : 0u, 1);

    /// <summary>
    /// Emits the trailing partial byte, zero padded.
    /// Returns the total byte count, or -1 if the buffer overflowed at any point.
    /// </summary>
    public int Flush()
    {
        if (_bitCount > 0 && !_overflowed)
        {
            if (_position >= _destination.Length)
            {
                _overflowed = true;
            }
            else
            {
                _destination[_position++] = (byte)_accumulator;
                _accumulator = 0;
                _bitCount = 0;
            }
        }

        return _overflowed ? -1 : _position;
    }
}
