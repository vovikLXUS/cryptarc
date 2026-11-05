using System.Buffers.Binary;
using CryptArc.Core;
using CryptArc.Core.Codecs;

namespace CryptArc.Tests.Codecs;

public class ArithmeticCodecTests
{
    private readonly ArithmeticCodec _codec = new();

    [Fact]
    public void EmptyInputProducesZeroBytes()
    {
        byte[] destination = new byte[16];
        int encoded = _codec.Encode([], destination);
        Assert.Equal(0, encoded);

        byte[] restored = new byte[16];
        int decoded = _codec.Decode([], restored);
        Assert.Equal(0, decoded);
    }

    [Fact]
    public void SingleByteRoundTrips()
    {
        byte[] original = [0x5A];
        byte[] compressed = new byte[_codec.MaxCompressedSize(original.Length)];
        int compressedLength = _codec.Encode(original, compressed);

        Assert.True(compressedLength > 0);

        byte[] restored = new byte[1];
        int restoredLength = _codec.Decode(compressed.AsSpan(0, compressedLength), restored);

        Assert.Equal(1, restoredLength);
        Assert.Equal(original[0], restored[0]);
    }

    [Fact]
    public void NaiveEncodeProducesBitIdenticalOutputToOptimized()
    {
        byte[] original = TestData.Text();

        byte[] compressedOptimized = new byte[_codec.MaxCompressedSize(original.Length)];
        int optLen = _codec.Encode(original, compressedOptimized);

        byte[] compressedNaive = new byte[_codec.MaxCompressedSize(original.Length)];
        int naiveLen = _codec.EncodeNaive(original, compressedNaive);

        Assert.Equal(optLen, naiveLen);
        Assert.True(compressedOptimized.AsSpan(0, optLen).SequenceEqual(compressedNaive.AsSpan(0, naiveLen)));
    }

    [Fact]
    public void NaiveDecodeProducesIdenticalOutputToOptimized()
    {
        byte[] original = TestData.Mixed(5000);

        byte[] compressed = new byte[_codec.MaxCompressedSize(original.Length)];
        int compLen = _codec.Encode(original, compressed);

        byte[] restoredOpt = new byte[original.Length];
        int optDecLen = _codec.Decode(compressed.AsSpan(0, compLen), restoredOpt);

        byte[] restoredNaive = new byte[original.Length];
        int naiveDecLen = _codec.DecodeNaive(compressed.AsSpan(0, compLen), restoredNaive);

        Assert.Equal(original.Length, optDecLen);
        Assert.Equal(original.Length, naiveDecLen);
        Assert.Equal(original, restoredOpt);
        Assert.Equal(original, restoredNaive);
    }

    [Fact]
    public void HandlesLargeInputTriggeringFrequencyRescaling()
    {
        // MaxTotal is 16,384, so 40,000 bytes will trigger rescaling multiple times
        byte[] original = TestData.Mixed(40_000);

        byte[] compressed = new byte[_codec.MaxCompressedSize(original.Length)];
        int compLen = _codec.Encode(original, compressed);

        Assert.True(compLen > 0);

        byte[] restored = new byte[original.Length];
        int decLen = _codec.Decode(compressed.AsSpan(0, compLen), restored);

        Assert.Equal(original.Length, decLen);
        Assert.Equal(original, restored);
    }

    [Fact]
    public void CompressesTextSubstantially()
    {
        byte[] original = TestData.Text();
        byte[] compressed = new byte[_codec.MaxCompressedSize(original.Length)];
        int compLen = _codec.Encode(original, compressed);

        double ratio = compLen / (double)original.Length;
        // Text has low entropy, should compress well under 75%
        Assert.True(ratio < 0.75, $"Expected ratio < 0.75, but got {ratio:P1}");
    }

    [Fact]
    public void RejectsTruncatedHeader()
    {
        byte[] malformed = [0x01, 0x02]; // Fewer than 4 bytes
        Assert.Throws<InvalidDataException>(() => _codec.Decode(malformed, new byte[16]));
    }

    [Fact]
    public void RejectsDestinationBufferTooSmall()
    {
        byte[] headerOnly = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(headerOnly, 100); // Claims 100 bytes

        Assert.Throws<InvalidDataException>(() => _codec.Decode(headerOnly, new byte[50]));
    }

    [Fact]
    public void RejectsTruncatedCompressedStream()
    {
        byte[] original = TestData.Text();
        byte[] compressed = new byte[_codec.MaxCompressedSize(original.Length)];
        int compLen = _codec.Encode(original, compressed);

        // Truncate the compressed payload by removing half of the bits
        byte[] truncated = compressed.AsSpan(0, compLen / 2).ToArray();

        Assert.Throws<InvalidDataException>(() => _codec.Decode(truncated, new byte[original.Length]));
    }

    [Fact]
    public void ReturnsMinusOneWhenDestinationBufferTooSmall()
    {
        byte[] original = TestData.Text();
        byte[] tooSmall = new byte[10];

        int result = _codec.Encode(original, tooSmall);
        Assert.Equal(-1, result);
    }
}

