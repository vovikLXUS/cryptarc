using CryptArc.Core.Coding;

namespace CryptArc.Tests.Coding;

/// <summary>
/// Back-reference expansion. The block-at-a-time version must agree with the
/// one-byte-at-a-time one for every shape of overlap, which is the whole reason the naive
/// version is kept.
/// </summary>
public class LzOutputTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 258)]
    [InlineData(2, 3)]
    [InlineData(2, 258)]
    [InlineData(3, 4)]
    [InlineData(45, 258)]
    [InlineData(100, 101)]
    [InlineData(128, 258)]
    [InlineData(257, 258)]
    [InlineData(258, 258)]
    [InlineData(300, 258)]
    public void BlockCopyAgreesWithByteByByte(int distance, int length)
    {
        byte[] prefix = TestData.Random(512, seed: 17);

        byte[] fast = BuildBuffer(prefix, length);
        byte[] naive = BuildBuffer(prefix, length);

        LzOutput.CopyMatch(fast, prefix.Length, distance, length);
        LzOutput.CopyMatchNaive(naive, prefix.Length, distance, length);

        Assert.Equal(naive, fast);
    }

    [Fact]
    public void ADistanceOfOneRepeatsASingleByte()
    {
        byte[] buffer = new byte[10];
        buffer[0] = 0x7F;

        LzOutput.CopyMatch(buffer, 1, distance: 1, length: 9);

        Assert.All(buffer, value => Assert.Equal(0x7F, value));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void AnOverlappingCopyRepeatsThePattern(int distance)
    {
        byte[] prefix = TestData.Random(distance, seed: 5);
        byte[] buffer = BuildBuffer(prefix, 100);

        LzOutput.CopyMatch(buffer, distance, distance, 100);

        // Every byte of the expansion is the pattern repeated, which is what makes the
        // block-at-a-time copy legal: the source of each block is already written.
        for (int i = 0; i < 100; i++)
            Assert.Equal(prefix[i % distance], buffer[distance + i]);
    }

    private static byte[] BuildBuffer(byte[] prefix, int length)
    {
        byte[] buffer = new byte[prefix.Length + length];
        prefix.CopyTo(buffer, 0);
        return buffer;
    }
}
