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
}
