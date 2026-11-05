using CryptArc.Core.Codecs;

namespace CryptArc.Tests.Codecs;

public class BwtCodecTests
{
    private readonly BwtCodec _codec = new();
    private readonly HuffmanCodec _huffman = new();

    [Fact]
    public void EncodesEmptyInputAsNothing()
    {
        Assert.Equal(0, _codec.Encode([], new byte[16]));
        Assert.Equal(0, _codec.Decode([], []));
    }

    [Fact]
    public void RejectsEmptyStreamForANonEmptyBlock()
    {
        Assert.Throws<InvalidDataException>(() => _codec.Decode([], new byte[16]));
    }

    [Fact]
    public void CompressesRepetitiveTextFarBetterThanHuffmanAlone()
    {
        byte[] text = TestData.Text();

        int bwt = Encode(_codec, text).Length;
        int huffman = Encode(_huffman, text).Length;

        Assert.True(bwt * 10 < huffman, $"BWT {bwt} bytes vs Huffman {huffman} bytes.");
    }

    [Fact]
    public void CollapsesAConstantBlockToAFewBytes()
    {
        byte[] compressed = Encode(_codec, TestData.Constant(256 * 1024));

        // Mode, primary index, the Huffman header and a handful of run digits.
        Assert.InRange(compressed.Length, 1, 160);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(100)]
    [InlineData(5000)]
    public void StoresIncompressibleInputWithOneByteOfOverhead(int size)
    {
        byte[] original = TestData.Random(size);

        byte[] compressed = Encode(_codec, original);

        Assert.Equal(size + 1, compressed.Length);
        Assert.Equal(1, compressed[0]);
        Assert.Equal(original, compressed[1..]);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1000)]
    [InlineData(65_537)]
    public void RoundTripsSizesAroundTheHeaderAndAcrossManyRounds(int size)
    {
        foreach (byte[] original in new[] { TestData.Text().AsSpan(0, Math.Min(size, 9000)).ToArray(), TestData.Mixed(size) })
        {
            byte[] compressed = Encode(_codec, original);
            byte[] restored = new byte[original.Length];

            Assert.Equal(original.Length, _codec.Decode(compressed, restored));
            Assert.Equal(original, restored);
        }
    }

    [Fact]
    public void ReportsFailureInsteadOfOverrunningTheBuffer()
    {
        byte[] text = TestData.Text();

        Assert.Equal(-1, _codec.Encode(text, new byte[8]));
        Assert.Equal(-1, _codec.Encode(TestData.Random(64), new byte[64]));
    }

    [Fact]
    public void RejectsUnknownMode()
    {
        Assert.Throws<InvalidDataException>(() => _codec.Decode([2, 0, 0, 0, 0], new byte[16]));
    }

    [Fact]
    public void RejectsTruncatedHeader()
    {
        Assert.Throws<InvalidDataException>(() => _codec.Decode([0, 1, 0], new byte[16]));
    }

    [Fact]
    public void RejectsStoredBlockLargerThanTheBlockSize()
    {
        Assert.Throws<InvalidDataException>(() => _codec.Decode([1, 1, 2, 3], new byte[2]));
    }

    [Fact]
    public void RejectsEveryTruncationOfAStoredBlock()
    {
        byte[] original = TestData.Random(300);
        byte[] compressed = Encode(_codec, original);
        Assert.Equal(1, compressed[0]);

        for (int length = 1; length < compressed.Length; length++)
        {
            byte[] truncated = compressed[..length];
            Assert.Throws<InvalidDataException>(() => _codec.Decode(truncated, new byte[original.Length]));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void RejectsATransformedBlockOfTheWrongSize(int delta)
    {
        byte[] original = TestData.Text();
        byte[] compressed = Encode(_codec, original);
        Assert.Equal(0, compressed[0]);

        Assert.Throws<InvalidDataException>(() => _codec.Decode(compressed, new byte[original.Length + delta]));
    }

    [Fact]
    public void RejectsPrimaryIndexOutOfRange()
    {
        byte[] compressed = Encode(_codec, TestData.Text());
        compressed[1] = 0xFF;
        compressed[2] = 0xFF;

        Assert.Throws<InvalidDataException>(() => _codec.Decode(compressed, new byte[9000]));
    }

    [Fact]
    public void RejectsEveryTruncation()
    {
        byte[] compressed = Encode(_codec, TestData.Text());
        byte[] restored = new byte[9000];

        for (int length = 1; length < compressed.Length; length++)
        {
            byte[] truncated = compressed[..length];
            Assert.Throws<InvalidDataException>(() => _codec.Decode(truncated, restored));
        }
    }

    [Fact]
    public void SurvivesCorruptionWithoutCrashing()
    {
        byte[] original = TestData.Mixed(20_000);
        byte[] compressed = Encode(_codec, original);
        byte[] restored = new byte[original.Length];
        var random = new Random(7);

        Assert.Equal(0, compressed[0]);

        for (int trial = 0; trial < 500; trial++)
        {
            byte[] corrupt = (byte[])compressed.Clone();
            corrupt[random.Next(corrupt.Length)] ^= (byte)(1 << random.Next(8));

            // Either a clean rejection or some output; never another exception type.
            try
            {
                _codec.Decode(corrupt, restored);
            }
            catch (InvalidDataException)
            {
            }
        }
    }

    private static byte[] Encode(Core.ICodec codec, byte[] original)
    {
        byte[] compressed = new byte[codec.MaxCompressedSize(original.Length)];
        int length = codec.Encode(original, compressed);
        Assert.True(length >= 0);
        return compressed[..length];
    }
}
