using CryptArc.Core.Transforms;

namespace CryptArc.Tests.Transforms;

public class MoveToFrontTests
{
    [Fact]
    public void EncodesTheWorkedExample()
    {
        // b is at 98; once b moves to the front a is pushed to 98 too; n is at 110;
        // after that the two letters swap places at 1, and repeats become 0.
        byte[] data = "bananaaa"u8.ToArray();

        MoveToFront.Encode(data);

        Assert.Equal([98, 98, 110, 1, 1, 1, 0, 0], data);
    }

    [Fact]
    public void TurnsRepeatsIntoZeros()
    {
        byte[] data = TestData.Constant(1000, 0xC3);

        MoveToFront.Encode(data);

        Assert.Equal(0xC3, data[0]);
        Assert.All(data[1..], value => Assert.Equal(0, value));
    }

    [Fact]
    public void ReachesTheLastPositionWhenCyclingThroughEveryByte()
    {
        byte[] data = new byte[512];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)i;

        MoveToFront.Encode(data);

        Assert.All(data[256..], value => Assert.Equal(255, value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5000)]
    public void BlockMoveAgreesWithThePlainLoop(int size)
    {
        foreach (byte[] original in new[] { TestData.Random(size), TestData.Mixed(size), TestData.Text() })
        {
            byte[] fast = (byte[])original.Clone();
            byte[] naive = (byte[])original.Clone();

            MoveToFront.Encode(fast);
            MoveToFront.EncodeNaive(naive);
            Assert.Equal(naive, fast);

            MoveToFront.Decode(fast);
            MoveToFront.DecodeNaive(naive);
            Assert.Equal(original, naive);
            Assert.Equal(original, fast);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5000)]
    public void DecodeInvertsEncode(int size)
    {
        foreach (byte[] original in new[] { TestData.Random(size), TestData.Mixed(size), TestData.Alternating(size) })
        {
            byte[] data = (byte[])original.Clone();

            MoveToFront.Encode(data);
            MoveToFront.Decode(data);

            Assert.Equal(original, data);
        }
    }
}
