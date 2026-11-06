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

    /// <summary>Unbounded chain and greedy matching: the settings under which the two encoders must agree.</summary>
    private static readonly DeflateCodec Exhaustive = new(int.MaxValue, lazyMatching: false);

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

    [Theory]
    [InlineData("single")]
    [InlineData("constant")]
    [InlineData("alternating")]
    [InlineData("random")]
    [InlineData("text")]
    public void NaiveAndOptimisedProduceIdenticalBytes(string sample)
    {
        byte[] original = Sample(sample);

        byte[] optimised = new byte[Codec.MaxCompressedSize(original.Length)];
        byte[] naive = new byte[Codec.MaxCompressedSize(original.Length)];

        int optimisedLength = Exhaustive.Encode(original, optimised);
        int naiveLength = DeflateCodec.EncodeNaive(original, naive);

        Assert.Equal(naiveLength, optimisedLength);
        Assert.Equal(naive[..naiveLength], optimised[..optimisedLength]);
    }

    [Fact]
    public void CompressesRepetitiveTextWell()
    {
        byte[] original = TestData.Text();
        byte[] compressed = new byte[Codec.MaxCompressedSize(original.Length)];

        int length = Codec.Encode(original, compressed);

        Assert.True(length > 0);
        Assert.True(length < original.Length / 10,
            $"Expected under {original.Length / 10} bytes for repetitive text, got {length}.");

        byte[] restored = new byte[original.Length];
        Assert.Equal(original.Length, Codec.Decode(compressed.AsSpan(0, length), restored));
        Assert.Equal(original, restored);
    }

    [Fact]
    public void EmptyInputIsStillAValidStreamOnItsOwn()
    {
        byte[] compressed = new byte[Codec.MaxCompressedSize(0)];

        int length = Codec.Encode([], compressed);

        // An empty fixed block: three header bits and the 7-bit end-of-block code.
        Assert.Equal(2, length);
        Assert.Empty(ReferenceInflate(compressed.AsSpan(0, length)));
        Assert.Equal(0, Codec.Decode(compressed.AsSpan(0, length), []));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(257)]
    [InlineData(258)]
    [InlineData(259)]
    public void HandlesMatchLengthsAroundTheFormatLimits(int runLength)
    {
        // A run of identical bytes becomes a match of distance 1, which is the only way to
        // reach the 258-byte maximum from a short input.
        byte[] original = new byte[runLength + 4];
        original.AsSpan().Fill(0x5A);
        original[0] = 0x01;

        byte[] compressed = new byte[Codec.MaxCompressedSize(original.Length)];
        int length = Codec.Encode(original, compressed);

        Assert.True(length >= 0);
        Assert.Equal(original, ReferenceInflate(compressed.AsSpan(0, length)));

        byte[] restored = new byte[original.Length];
        Assert.Equal(original.Length, Codec.Decode(compressed.AsSpan(0, length), restored));
        Assert.Equal(original, restored);
    }

    [Fact]
    public void StoredBlockFramingMatchesTheSpecification()
    {
        // Random data does not compress, so the encoder falls back to a stored block.
        byte[] original = TestData.Random(5000);
        byte[] compressed = new byte[Codec.MaxCompressedSize(original.Length)];

        int length = Codec.Encode(original, compressed);

        Assert.Equal(original.Length + 5, length);

        // BFINAL = 1, BTYPE = 00, then five padding bits to the byte boundary.
        Assert.Equal(0x01, compressed[0]);

        // LEN = 5000 = 0x1388 and NLEN = 0xEC77, both little-endian.
        Assert.Equal(0x88, compressed[1]);
        Assert.Equal(0x13, compressed[2]);
        Assert.Equal(0x77, compressed[3]);
        Assert.Equal(0xEC, compressed[4]);

        Assert.Equal(original, compressed[5..length]);
    }

    [Fact]
    public void SplitsLongIncompressibleInputIntoSeveralStoredBlocks()
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
    public void ReadsAReferenceStoredStream()
    {
        byte[] original = TestData.Mixed(20_000);
        byte[] reference = ReferenceDeflate(original, CompressionLevel.NoCompression);

        byte[] restored = new byte[original.Length];

        Assert.Equal(original.Length, Codec.Decode(reference, restored));
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

    [Fact]
    public void DynamicBlocksAreNotSupportedYet()
    {
        // BFINAL = 1, BTYPE = 10. Delete this test when dynamic blocks land.
        byte[] dynamicHeader = [0x05];

        Assert.Throws<NotSupportedException>(() => Codec.Decode(dynamicHeader, new byte[8]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(10)]
    public void RejectsATruncatedStoredStream(int keep)
    {
        byte[] original = TestData.Random(5000);
        byte[] compressed = new byte[Codec.MaxCompressedSize(original.Length)];
        int length = Codec.Encode(original, compressed);

        Assert.True(keep < length);
        Assert.Throws<InvalidDataException>(
            () => Codec.Decode(compressed.AsSpan(0, keep), new byte[original.Length]));
    }

    [Fact]
    public void RejectsAStoredBlockLongerThanTheDestination()
    {
        byte[] original = TestData.Random(5000);
        byte[] compressed = new byte[Codec.MaxCompressedSize(original.Length)];
        int length = Codec.Encode(original, compressed);

        Assert.Throws<InvalidDataException>(
            () => Codec.Decode(compressed.AsSpan(0, length), new byte[16]));
    }

    [Fact]
    public void CorruptFixedStreamsOnlyEverRaiseInvalidData()
    {
        byte[] original = TestData.Text();
        byte[] compressed = new byte[Codec.MaxCompressedSize(original.Length)];
        int length = Codec.Encode(original, compressed);

        byte[] restored = new byte[original.Length];

        // Every single-byte corruption must either decode to something or be rejected
        // cleanly. It must never read out of bounds, loop forever or throw anything else.
        for (int i = 0; i < Math.Min(length, 200); i++)
        {
            byte[] corrupt = compressed[..length];
            corrupt[i] ^= 0xFF;

            try
            {
                Codec.Decode(corrupt, restored);
            }
            catch (InvalidDataException)
            {
            }
            catch (NotSupportedException)
            {
                // Flipping the type bits can name a dynamic block.
            }
        }
    }

    [Fact]
    public void RejectsATruncatedFixedStream()
    {
        byte[] original = TestData.Text();
        byte[] compressed = new byte[Codec.MaxCompressedSize(original.Length)];
        int length = Codec.Encode(original, compressed);

        Assert.True(length > 8, "Repetitive text should compress to a fixed block.");

        byte[] restored = new byte[original.Length];

        Assert.Throws<InvalidDataException>(
            () => Codec.Decode(compressed.AsSpan(0, length - 1), restored));
    }

    [Fact]
    public void MaxCompressedSizeRejectsNegativeLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Codec.MaxCompressedSize(-1));
    }

    [Fact]
    public void RejectsAChainLimitBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeflateCodec(0));
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
    private static byte[] ReferenceDeflate(byte[] data, CompressionLevel level)
    {
        using var output = new MemoryStream();

        using (var deflater = new DeflateStream(output, level, leaveOpen: true))
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
