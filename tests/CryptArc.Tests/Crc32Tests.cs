using System.Text;
using CryptArc.Core.Hashing;

namespace CryptArc.Tests;

public class Crc32Tests
{
    [Fact]
    public void MatchesTheStandardCheckValue()
    {
        // The check value published with the CRC-32/ISO-HDLC specification.
        Assert.Equal(0xCBF43926u, Crc32.Compute(Encoding.ASCII.GetBytes("123456789")));
    }

    [Fact]
    public void EmptyInputHashesToZero()
    {
        Assert.Equal(0u, Crc32.Compute([]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1023)]
    [InlineData(65536)]
    public void TableAndNaiveImplementationsAgree(int length)
    {
        byte[] data = TestData.Random(length, seed: length + 1);

        Assert.Equal(Crc32.ComputeNaive(data), Crc32.Compute(data));
    }
}
