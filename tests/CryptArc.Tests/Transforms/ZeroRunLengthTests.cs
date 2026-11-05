using CryptArc.Core.Transforms;

namespace CryptArc.Tests.Transforms;

public class ZeroRunLengthTests
{
    [Theory]
    [InlineData(1, new byte[] { 0 })]
    [InlineData(2, new byte[] { 1 })]
    [InlineData(3, new byte[] { 0, 0 })]
    [InlineData(4, new byte[] { 1, 0 })]
    [InlineData(5, new byte[] { 0, 1 })]
    [InlineData(6, new byte[] { 1, 1 })]
    [InlineData(7, new byte[] { 0, 0, 0 })]
    public void WritesRunLengthsInBijectiveBaseTwo(int run, byte[] expected)
    {
        byte[] encoded = new byte[ZeroRunLength.MaxEncodedSize(run)];

        int length = ZeroRunLength.Encode(new byte[run], encoded);

        Assert.Equal(expected, encoded[..length]);
    }

    [Fact]
    public void ShiftsValuesAndEscapesTheTopTwo()
    {
        byte[] source = [1, 2, 253, 254, 255];
        byte[] encoded = new byte[ZeroRunLength.MaxEncodedSize(source.Length)];

        int length = ZeroRunLength.Encode(source, encoded);

        Assert.Equal([2, 3, 254, 255, 0, 255, 1], encoded[..length]);
    }

    [Fact]
    public void CollapsesALongRunToItsLogarithm()
    {
        byte[] encoded = new byte[ZeroRunLength.MaxEncodedSize(1_000_000)];

        int length = ZeroRunLength.Encode(new byte[1_000_000], encoded);

        // floor(log2(1_000_001)) = 19
        Assert.Equal(19, length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(254)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(65_535)]
    [InlineData(65_536)]
    public void RoundTripsRunsAroundEveryDigitBoundary(int run)
    {
        byte[] original = [7, .. new byte[run], 254, .. new byte[run], 255];

        Assert.Equal(original, RoundTrip(original));
    }

    [Fact]
    public void RoundTripsEveryByteValue()
    {
        byte[] original = new byte[1024];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i * 37);

        Assert.Equal(original, RoundTrip(original));
    }

    [Fact]
    public void NeverExceedsTheDeclaredUpperBound()
    {
        byte[] worst = TestData.Constant(1000, 255);
        byte[] encoded = new byte[ZeroRunLength.MaxEncodedSize(worst.Length)];

        Assert.Equal(2000, ZeroRunLength.Encode(worst, encoded));
    }

    [Fact]
    public void ReportsFailureInsteadOfOverrunningTheBuffer()
    {
        Assert.Equal(-1, ZeroRunLength.Encode([5, 6, 7], new byte[2]));
        Assert.Equal(-1, ZeroRunLength.Encode([255], new byte[1]));
        Assert.Equal(-1, ZeroRunLength.Encode(new byte[7], new byte[2]));
    }

    [Fact]
    public void RejectsTruncatedEscape()
    {
        Assert.Throws<InvalidDataException>(() => ZeroRunLength.Decode([2, 255], new byte[16]));
    }

    [Fact]
    public void RejectsImpossibleEscape()
    {
        Assert.Throws<InvalidDataException>(() => ZeroRunLength.Decode([255, 2], new byte[16]));
    }

    [Fact]
    public void RejectsRunThatExpandsPastTheBlockSize()
    {
        // Digits 0, 0, 0, 0, 0 announce a run of 31 zeros.
        Assert.Throws<InvalidDataException>(() => ZeroRunLength.Decode([0, 0, 0, 0, 0], new byte[30]));
    }

    [Fact]
    public void RejectsHugeRunWithoutOverflowing()
    {
        byte[] malformed = new byte[200];
        malformed.AsSpan().Fill(1);

        Assert.Throws<InvalidDataException>(() => ZeroRunLength.Decode(malformed, new byte[1 << 20]));
    }

    [Fact]
    public void RejectsValuesThatExpandPastTheBlockSize()
    {
        Assert.Throws<InvalidDataException>(() => ZeroRunLength.Decode([2, 3, 4], new byte[2]));
    }

    private static byte[] RoundTrip(byte[] original)
    {
        byte[] encoded = new byte[ZeroRunLength.MaxEncodedSize(original.Length)];
        int length = ZeroRunLength.Encode(original, encoded);

        byte[] decoded = new byte[original.Length];
        int written = ZeroRunLength.Decode(encoded.AsSpan(0, length), decoded);

        Assert.Equal(original.Length, written);
        return decoded;
    }
}
