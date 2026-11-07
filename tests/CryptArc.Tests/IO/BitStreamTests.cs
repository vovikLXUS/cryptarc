using CryptArc.Core.IO;

namespace CryptArc.Tests.IO;

public class BitStreamTests
{
    [Fact]
    public void RoundTripsVariableWidthValues()
    {
        (uint Value, int Width)[] symbols =
        [
            (0b1, 1), (0b101, 3), (0xFF, 8), (0x1234, 16), (0x7, 3), (0xDEADBEEF, 32), (0, 5),
        ];

        byte[] buffer = new byte[64];
        var writer = new BitWriter(buffer);

        foreach ((uint value, int width) in symbols)
            writer.WriteBits(value, width);

        int written = writer.Flush();
        Assert.True(written > 0);

        var reader = new BitReader(buffer.AsSpan(0, written));

        foreach ((uint value, int width) in symbols)
            Assert.Equal(value, reader.ReadBits(width));
    }

    [Fact]
    public void RoundTripsALongRandomBitPattern()
    {
        var random = new Random(7);
        bool[] bits = Enumerable.Range(0, 10_000).Select(_ => random.Next(2) == 1).ToArray();

        byte[] buffer = new byte[bits.Length / 8 + 1];
        var writer = new BitWriter(buffer);

        foreach (bool bit in bits)
            writer.WriteBit(bit);

        int written = writer.Flush();
        var reader = new BitReader(buffer.AsSpan(0, written));

        foreach (bool bit in bits)
            Assert.Equal(bit, reader.ReadBit());
    }

    [Fact]
    public void SignalsOverflowInsteadOfWritingOutOfBounds()
    {
        byte[] buffer = new byte[2];
        var writer = new BitWriter(buffer);

        for (int i = 0; i < 100; i++)
            writer.WriteBits(0xFF, 8);

        Assert.True(writer.Overflowed);
        Assert.Equal(-1, writer.Flush());
    }

    [Fact]
    public void ZeroExtendsPastTheEndOfInput()
    {
        var reader = new BitReader([0xFF]);

        Assert.Equal(0xFFu, reader.ReadBits(8));
        Assert.Equal(0u, reader.ReadBits(8));
    }

    [Fact]
    public void PeekDoesNotConsumeAndSkipDoes()
    {
        var reader = new BitReader([0b1010_0110, 0xFF]);

        Assert.Equal(0b0110u, reader.PeekBits(4));
        Assert.Equal(0b0110u, reader.PeekBits(4));

        reader.SkipBits(4);

        Assert.Equal(0b1010u, reader.ReadBits(4));
        Assert.Equal(0xFFu, reader.ReadBits(8));
    }

    [Fact]
    public void PeekingPastTheEndIsNotAnOverrun()
    {
        var reader = new BitReader([0xFF]);

        reader.SkipBits(4);

        Assert.Equal(0x0Fu, reader.PeekBits(16));
        Assert.False(reader.IsOverrun);
    }

    [Fact]
    public void FlagsAnOverrunOnlyOnceAPaddingBitIsConsumed()
    {
        var reader = new BitReader([0xFF]);

        reader.ReadBits(8);
        Assert.False(reader.IsOverrun);

        reader.ReadBit();
        Assert.True(reader.IsOverrun);
    }

    [Fact]
    public void AlignToByteFinishesThePartialByteWithZeros()
    {
        byte[] buffer = new byte[4];
        var writer = new BitWriter(buffer);

        writer.WriteBits(0b111, 3);
        writer.AlignToByte();

        Assert.Equal(1, writer.BytesWritten);

        writer.WriteBits(0xA5, 8);

        Assert.Equal(2, writer.Flush());
        Assert.Equal(0b0000_0111, buffer[0]);
        Assert.Equal(0xA5, buffer[1]);
    }

    [Fact]
    public void AlignToByteIsANoOpWhenAlreadyAligned()
    {
        byte[] buffer = new byte[4];
        var writer = new BitWriter(buffer);

        writer.WriteBits(0xC3, 8);
        writer.AlignToByte();
        writer.AlignToByte();

        Assert.Equal(1, writer.Flush());
        Assert.Equal(0xC3, buffer[0]);
    }

    [Fact]
    public void AlignedPayloadSurvivesARoundTrip()
    {
        byte[] payload = [0x00, 0x7F, 0x80, 0xFF, 0x42];
        byte[] buffer = new byte[16];

        var writer = new BitWriter(buffer);
        writer.WriteBits(0b10101, 5);
        writer.AlignToByte();

        foreach (byte value in payload)
            writer.WriteBits(value, 8);

        int written = writer.Flush();
        Assert.True(written > 0);

        var reader = new BitReader(buffer.AsSpan(0, written));
        Assert.Equal(0b10101u, reader.ReadBits(5));
        reader.AlignToByte();

        foreach (byte value in payload)
            Assert.Equal(value, reader.ReadBits(8));

        Assert.False(reader.IsOverrun);
    }

    [Fact]
    public void ReaderAlignToByteSkipsOnlyThePadding()
    {
        // 3 bits, then 5 bits of padding, then two whole bytes.
        var reader = new BitReader([0b0000_0101, 0x11, 0x22]);

        Assert.Equal(0b101u, reader.ReadBits(3));
        reader.AlignToByte();
        reader.AlignToByte();

        Assert.Equal(0x11u, reader.ReadBits(8));
        Assert.Equal(0x22u, reader.ReadBits(8));
        Assert.False(reader.IsOverrun);
    }
}
