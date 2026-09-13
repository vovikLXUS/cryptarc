using CryptArc.Core;
using CryptArc.Core.Codecs;

namespace CryptArc.Tests.Codecs;

public class RleCodecTests
{
    private readonly RleCodec _codec = new();

    [Fact]
    public void CollapsesLongRuns()
    {
        byte[] original = TestData.Constant(1300);
        byte[] compressed = new byte[_codec.MaxCompressedSize(original.Length)];

        int length = _codec.Encode(original, compressed);

        // 1300 bytes -> 10 groups of 130 -> 2 bytes each.
        Assert.Equal(20, length);
    }

    [Fact]
    public void ExpandsIncompressibleDataByLessThanOnePercent()
    {
        byte[] original = TestData.Alternating(100_000);
        byte[] compressed = new byte[_codec.MaxCompressedSize(original.Length)];

        int length = _codec.Encode(original, compressed);
        double overhead = (length - original.Length) / (double)original.Length;

        Assert.InRange(overhead, 0, 0.01);
    }

    [Fact]
    public void RejectsTruncatedRepeatRun()
    {
        // Control byte announcing a repeat run, but the value byte is missing.
        byte[] malformed = [0x80];

        Assert.Throws<InvalidDataException>(() => _codec.Decode(malformed, new byte[64]));
    }

    [Fact]
    public void RejectsStreamThatExpandsPastTheBlockSize()
    {
        // Announces a 130 byte run into a 16 byte destination.
        byte[] malformed = [0xFF, 0xAB];

        Assert.Throws<InvalidDataException>(() => _codec.Decode(malformed, new byte[16]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(130)]
    [InlineData(131)]
    [InlineData(4096)]
    public void HandlesRunLengthsAroundTheGroupBoundaries(int length)
    {
        byte[] original = TestData.Constant(length);
        byte[] compressed = new byte[_codec.MaxCompressedSize(length)];

        int compressedLength = _codec.Encode(original, compressed);
        byte[] restored = new byte[length];

        Assert.Equal(length, _codec.Decode(compressed.AsSpan(0, compressedLength), restored));
        Assert.Equal(original, restored);
    }
}
