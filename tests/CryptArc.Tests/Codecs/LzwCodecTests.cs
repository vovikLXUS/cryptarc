using CryptArc.Core;
using CryptArc.Core.Codecs;

namespace CryptArc.Tests.Codecs;

public class LzwCodecTests
{
    private readonly LzwCodec _codec = new();

    [Fact]
    public void CollapsesRepetitiveData()
    {
        byte[] original = TestData.Constant(10_000);
        byte[] compressed = new byte[_codec.MaxCompressedSize(original.Length)];

        int length = _codec.Encode(original, compressed);
        Assert.True(length > 0);

        double ratio = length / (double)original.Length;
        Assert.True(ratio < 0.05, $"Expected ratio < 5%, got {ratio:P2}");

        byte[] restored = new byte[original.Length];
        int restoredLength = _codec.Decode(compressed.AsSpan(0, length), restored);

        Assert.Equal(original.Length, restoredLength);
        Assert.Equal(original, restored);
    }

    [Fact]
    public void CompressesTextEfficiently()
    {
        byte[] original = TestData.Text();
        byte[] compressed = new byte[_codec.MaxCompressedSize(original.Length)];

        int length = _codec.Encode(original, compressed);
        Assert.True(length > 0);

        double ratio = length / (double)original.Length;
        Assert.True(ratio < 0.50, $"Expected text ratio < 50%, got {ratio:P2}");

        byte[] restored = new byte[original.Length];
        int restoredLength = _codec.Decode(compressed.AsSpan(0, length), restored);

        Assert.Equal(original.Length, restoredLength);
        Assert.Equal(original, restored);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("single")]
    [InlineData("constant")]
    [InlineData("alternating")]
    [InlineData("random")]
    [InlineData("mixed")]
    [InlineData("text")]
    public void NaiveAndOptimisedProduceIdenticalCompressedBytes(string sampleName)
    {
        byte[] original = Sample(sampleName);
        byte[] fastCompressed = new byte[_codec.MaxCompressedSize(original.Length)];
        byte[] naiveCompressed = new byte[_codec.MaxCompressedSize(original.Length)];

        int fastLength = _codec.Encode(original, fastCompressed);
        int naiveLength = _codec.EncodeNaive(original, naiveCompressed);

        Assert.Equal(naiveLength, fastLength);
        Assert.True(fastCompressed.AsSpan(0, fastLength).SequenceEqual(naiveCompressed.AsSpan(0, naiveLength)),
            $"Optimised and naive encoders diverged on sample '{sampleName}'.");
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("single")]
    [InlineData("constant")]
    [InlineData("alternating")]
    [InlineData("random")]
    [InlineData("mixed")]
    [InlineData("text")]
    public void NaiveAndOptimisedDecodersBothRestoreOriginal(string sampleName)
    {
        byte[] original = Sample(sampleName);
        byte[] compressed = new byte[_codec.MaxCompressedSize(original.Length)];

        int length = _codec.Encode(original, compressed);
        ReadOnlySpan<byte> compressedSpan = compressed.AsSpan(0, length);

        byte[] restoredFast = new byte[original.Length];
        byte[] restoredNaive = new byte[original.Length];

        int fastLength = _codec.Decode(compressedSpan, restoredFast);
        int naiveLength = _codec.DecodeNaive(compressedSpan, restoredNaive);

        Assert.Equal(original.Length, fastLength);
        Assert.Equal(original.Length, naiveLength);
        Assert.Equal(original, restoredFast);
        Assert.Equal(original, restoredNaive);
    }

    [Theory]
    [InlineData("ABABABA")]
    [InlineData("AAAAAAA")]
    [InlineData("abcabcabcabc")]
    [InlineData("banana_bandana_banana")]
    public void HandlesKwKwKSpecialCase(string text)
    {
        byte[] original = System.Text.Encoding.UTF8.GetBytes(text);
        byte[] compressed = new byte[_codec.MaxCompressedSize(original.Length)];

        int length = _codec.Encode(original, compressed);
        Assert.True(length > 0);

        byte[] restored = new byte[original.Length];
        int restoredLength = _codec.Decode(compressed.AsSpan(0, length), restored);

        Assert.Equal(original.Length, restoredLength);
        Assert.Equal(original, restored);
    }

    [Fact]
    public void HandlesDictionaryResetAcrossLargeStreams()
    {
        // 100 KiB of varied text triggers the 4096-code dictionary capacity and ClearCode resets repeatedly.
        byte[] original = System.Text.Encoding.UTF8.GetBytes(string.Concat(
            Enumerable.Repeat("cryptarc smart archiver: lzw table reset test string with varying words and numbers 12345; ", 1200)));

        byte[] compressed = new byte[_codec.MaxCompressedSize(original.Length)];
        int length = _codec.Encode(original, compressed);
        Assert.True(length > 0);

        byte[] restored = new byte[original.Length];
        int restoredLength = _codec.Decode(compressed.AsSpan(0, length), restored);

        Assert.Equal(original.Length, restoredLength);
        Assert.Equal(original, restored);
    }

    [Fact]
    public void ReportsFailureWhenOutputBufferTooSmall()
    {
        byte[] original = TestData.Random(4096);
        byte[] tiny = new byte[4];

        Assert.Equal(-1, _codec.Encode(original, tiny));
        Assert.Equal(-1, _codec.EncodeNaive(original, tiny));
    }

    [Fact]
    public void RejectsTruncatedStream()
    {
        byte[] original = TestData.Text();
        byte[] compressed = new byte[_codec.MaxCompressedSize(original.Length)];
        int length = _codec.Encode(original, compressed);

        // Truncating before the EOS marker must raise InvalidDataException
        byte[] truncated = compressed.AsSpan(0, length / 2).ToArray();

        Assert.Throws<InvalidDataException>(() => _codec.Decode(truncated, new byte[original.Length]));
        Assert.Throws<InvalidDataException>(() => _codec.DecodeNaive(truncated, new byte[original.Length]));
    }

    [Fact]
    public void RejectsStreamExpandingPastDestination()
    {
        byte[] original = TestData.Text();
        byte[] compressed = new byte[_codec.MaxCompressedSize(original.Length)];
        int length = _codec.Encode(original, compressed);

        byte[] tooSmall = new byte[original.Length / 2];

        Assert.Throws<InvalidDataException>(() => _codec.Decode(compressed.AsSpan(0, length), tooSmall));
        Assert.Throws<InvalidDataException>(() => _codec.DecodeNaive(compressed.AsSpan(0, length), tooSmall));
    }

    [Fact]
    public void RejectsCorruptCode()
    {
        byte[] original = "hello world"u8.ToArray();
        byte[] compressed = new byte[_codec.MaxCompressedSize(original.Length)];
        int length = _codec.Encode(original, compressed);

        // Corrupt bits inside the stream to inject an out-of-range code
        compressed[1] ^= 0xFF;
        compressed[2] ^= 0xFF;

        Assert.Throws<InvalidDataException>(() => _codec.Decode(compressed.AsSpan(0, length), new byte[256]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(513)]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(1025)]
    [InlineData(2047)]
    [InlineData(2048)]
    [InlineData(2049)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    public void HandlesBoundaryLengths(int length)
    {
        byte[] original = TestData.Alternating(length);
        byte[] compressed = new byte[_codec.MaxCompressedSize(length)];

        int compressedLength = _codec.Encode(original, compressed);
        Assert.True(compressedLength >= 0);

        byte[] restored = new byte[length];
        int restoredLength = _codec.Decode(compressed.AsSpan(0, compressedLength), restored);

        Assert.Equal(length, restoredLength);
        Assert.Equal(original, restored);
    }

    [Fact]
    public void MaxCompressedSize_RejectsNegativeLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _codec.MaxCompressedSize(-1));
    }

    private static byte[] Sample(string name) => name switch
    {
        "empty" => [],
        "single" => [0x7F],
        "constant" => TestData.Constant(5000),
        "alternating" => TestData.Alternating(5000),
        "random" => TestData.Random(5000),
        "mixed" => TestData.Mixed(20000),
        "text" => TestData.Text(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown sample."),
    };
}
