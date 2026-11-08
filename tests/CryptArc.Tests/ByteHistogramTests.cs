using CryptArc.Core.Analysis;

namespace CryptArc.Tests;

public class ByteHistogramTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(4097)]
    public void FourTableAndNaiveCountsAgree(int length)
    {
        byte[] data = TestData.Mixed(length);
        var fast = new long[256];
        var naive = new long[256];

        ByteHistogram.Count(data, fast);
        ByteHistogram.CountNaive(data, naive);

        Assert.Equal(naive, fast);
        Assert.Equal(length, fast.Sum());
    }

    [Fact]
    public void ARunOfOneValueCountsEveryByte()
    {
        var counts = new long[256];

        ByteHistogram.Count(TestData.Constant(1003, 0x7F), counts);

        Assert.Equal(1003, counts[0x7F]);
        Assert.Equal(1003, counts.Sum());
    }

    [Fact]
    public void CountsAreOverwrittenNotAccumulated()
    {
        var counts = new long[256];
        counts[5] = 99;

        ByteHistogram.Count([5, 5], counts);

        Assert.Equal(2, counts[5]);
    }

    [Fact]
    public void ShortCountSpanIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ByteHistogram.Count([1], new long[255]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ByteHistogram.CountNaive([1], new long[255]));
    }
}
