using System.IO.Compression;
using CryptArc.Core.Codecs;

namespace CryptArc.Tests.Codecs;

/// <summary>
/// Deflate is the one codec in this project that implements a published format, so
/// correctness is not only "our decoder reads our encoder": a reference implementation
/// has to read our output too. <see cref="ReferenceInflate"/> is that oracle.
/// </summary>
/// <remarks>
/// <see cref="DeflateStream"/> appears here and in <c>bench/</c> only. It is banned in
/// <c>src/</c> — see the hard rules in <c>AGENTS.md</c>. Using it as a conformance oracle
/// is the opposite of using it to compress: it is what proves we wrote Deflate correctly
/// rather than letting us avoid writing it.
/// </remarks>
public class DeflateCodecTests
{
    private static readonly DeflateCodec Codec = new();

    [Fact]
    public void StoredBlockFramingMatchesTheSpecification()
    {
        byte[] original = [0xAA, 0xBB, 0xCC];
        byte[] compressed = new byte[Codec.MaxCompressedSize(original.Length)];

        int length = Codec.Encode(original, compressed);

        Assert.Equal(8, length);

        // BFINAL = 1, BTYPE = 00, then five padding bits to the byte boundary.
        Assert.Equal(0x01, compressed[0]);

        // LEN = 3, NLEN = ~3, both little-endian.
        Assert.Equal(0x03, compressed[1]);
        Assert.Equal(0x00, compressed[2]);
        Assert.Equal(0xFC, compressed[3]);
        Assert.Equal(0xFF, compressed[4]);

        Assert.Equal(original, compressed[5..8]);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("single")]
    [InlineData("constant")]
    [InlineData("alternating")]
    [InlineData("random")]
    [InlineData("mixed")]
    [InlineData("text")]
    public void AReferenceDecoderReadsOurOutput(string sample)
    {
        byte[] original = Sample(sample);
        byte[] compressed = new byte[Codec.MaxCompressedSize(original.Length)];

        int length = Codec.Encode(original, compressed);

        Assert.True(length >= 0);
        Assert.Equal(original, ReferenceInflate(compressed.AsSpan(0, length)));
    }

    [Fact]
    public void EmptyInputIsStillAValidStreamOnItsOwn()
    {
        byte[] compressed = new byte[Codec.MaxCompressedSize(0)];

        int length = Codec.Encode([], compressed);

        Assert.Equal(5, length);
        Assert.Empty(ReferenceInflate(compressed.AsSpan(0, length)));
        Assert.Equal(0, Codec.Decode(compressed.AsSpan(0, length), []));
    }

    [Fact]
    public void SplitsLongInputIntoSeveralStoredBlocks()
    {
        byte[] original = TestData.Random(200_000);
        byte[] compressed = new byte[Codec.MaxCompressedSize(original.Length)];

        int length = Codec.Encode(original, compressed);

        // Four blocks: three full ones of 65535 bytes and a remainder, 5 bytes of framing each.
        Assert.Equal(original.Length + 4 * 5, length);
        Assert.Equal(original, ReferenceInflate(compressed.AsSpan(0, length)));

        byte[] restored = new byte[original.Length];
        Assert.Equal(original.Length, Codec.Decode(compressed.AsSpan(0, length), restored));
        Assert.Equal(original, restored);
    }

    [Fact]
    public void ReportsFailureInsteadOfOverrunningTheBuffer()
    {
        byte[] original = TestData.Random(4096);

        Assert.Equal(-1, Codec.Encode(original, new byte[8]));
    }

    [Fact]
    public void RejectsAStoredBlockWhoseComplementIsWrong()
    {
        // BFINAL = 1, BTYPE = 00, LEN = 3, NLEN = 0 — which is not ~3.
        byte[] corrupt = [0x01, 0x03, 0x00, 0x00, 0x00, 1, 2, 3];

        Assert.Throws<InvalidDataException>(() => Codec.Decode(corrupt, new byte[8]));
    }

    [Fact]
    public void RejectsTheReservedBlockType()
    {
        // BFINAL = 1, BTYPE = 11.
        byte[] corrupt = [0x07];

        Assert.Throws<InvalidDataException>(() => Codec.Decode(corrupt, new byte[8]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(10)]
    public void RejectsATruncatedStream(int keep)
    {
        byte[] original = TestData.Random(32);
        byte[] compressed = new byte[Codec.MaxCompressedSize(original.Length)];
        int length = Codec.Encode(original, compressed);

        Assert.True(keep < length);
        Assert.Throws<InvalidDataException>(
            () => Codec.Decode(compressed.AsSpan(0, keep), new byte[original.Length]));
    }

    [Fact]
    public void RejectsAStoredBlockLongerThanTheDestination()
    {
        byte[] original = TestData.Random(64);
        byte[] compressed = new byte[Codec.MaxCompressedSize(original.Length)];
        int length = Codec.Encode(original, compressed);

        Assert.Throws<InvalidDataException>(
            () => Codec.Decode(compressed.AsSpan(0, length), new byte[16]));
    }

    [Fact]
    public void HuffmanBlocksAreNotSupportedYet()
    {
        // The reference compressor emits fixed or dynamic blocks, never stored ones, for
        // data that compresses. Delete this test when those block types land.
        byte[] reference = ReferenceDeflate(TestData.Text());

        Assert.Throws<NotSupportedException>(
            () => Codec.Decode(reference, new byte[TestData.Text().Length]));
    }

    [Fact]
    public void MaxCompressedSizeRejectsNegativeLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Codec.MaxCompressedSize(-1));
    }

    /// <summary>Decompresses a raw deflate stream with the framework's implementation.</summary>
    private static byte[] ReferenceInflate(ReadOnlySpan<byte> deflated)
    {
        using var input = new MemoryStream(deflated.ToArray());
        using var inflater = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();

        inflater.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Compresses with the framework's implementation, for decoder conformance.</summary>
    private static byte[] ReferenceDeflate(byte[] data)
    {
        using var output = new MemoryStream();

        using (var deflater = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            deflater.Write(data, 0, data.Length);

        return output.ToArray();
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
