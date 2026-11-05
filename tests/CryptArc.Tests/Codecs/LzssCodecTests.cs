using CryptArc.Core.Codecs;

namespace CryptArc.Tests.Codecs;

public class LzssCodecTests
{
    private readonly LzssCodec _codec = new();

    public static TheoryData<string> Samples() => new() { "empty", "single", "constant", "alternating", "random", "mixed", "text", "far-repeat", "window-edge", "past-window" };

    [Theory]
    [MemberData(nameof(Samples))]
    public void HashChainsAgreeWithTheLinearScanWhenUnboundedAndGreedy(string sample)
    {
        byte[] original = Sample(sample);
        var exhaustive = new LzssCodec(int.MaxValue, lazyMatching: false);

        byte[] expected = Encode(original, LzssCodec.EncodeNaive);
        byte[] actual = Encode(original, exhaustive.Encode);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void NaiveOutputRoundTrips(string sample)
    {
        byte[] original = Sample(sample);

        Assert.Equal(original, Decode(Encode(original, LzssCodec.EncodeNaive), original.Length));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    [InlineData(LzssCodec.DefaultMaxChainLength, false)]
    public void EverySearchSettingRoundTrips(int maxChainLength, bool lazy)
    {
        var codec = new LzssCodec(maxChainLength, lazy);

        foreach (string sample in new[] { "mixed", "text", "far-repeat" })
        {
            byte[] original = Sample(sample);
            Assert.Equal(original, Decode(Encode(original, codec.Encode), original.Length));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(130)]
    [InlineData(131)]
    public void WideMatchLengthAgreesWithBytewise(int firstDifference)
    {
        byte[] left = TestData.Random(LzssCodec.MaxMatch + 1, seed: firstDifference);
        byte[] right = (byte[])left.Clone();
        right[firstDifference] ^= 0x01;

        for (int limit = 0; limit <= left.Length; limit++)
        {
            Assert.Equal(
                LzssCodec.MatchLengthNaive(left, right, limit),
                LzssCodec.MatchLength(left, right, limit));
        }
    }

    [Fact]
    public void MatchLengthRejectsALimitPastTheInput()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LzssCodec.MatchLength(new byte[4], new byte[8], 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => LzssCodec.MatchLengthNaive(new byte[4], new byte[4], -1));
    }

    [Fact]
    public void RejectsAChainLimitBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LzssCodec(maxChainLength: 0));
    }

    [Fact]
    public void CollapsesARunIntoMaximalMatches()
    {
        // 1 literal, then distance-1 matches of 131 bytes: 1 + 1 + 15 * 3 = 47.
        byte[] original = TestData.Constant(1 + 15 * LzssCodec.MaxMatch);

        Assert.Equal(47, Encode(original, _codec.Encode).Length);
    }

    [Fact]
    public void CompressesRepetitiveText()
    {
        byte[] original = TestData.Text();

        Assert.True(Encode(original, _codec.Encode).Length < original.Length / 20);
    }

    [Fact]
    public void ExpandsIncompressibleDataByLessThanOnePercent()
    {
        byte[] original = TestData.Random(100_000);
        byte[] compressed = Encode(original, _codec.Encode);
        double overhead = (compressed.Length - original.Length) / (double)original.Length;

        Assert.InRange(overhead, 0, 0.008);
    }

    [Theory]
    [InlineData(LzssCodec.WindowSize, true)]
    [InlineData(LzssCodec.WindowSize + 1, false)]
    public void ReachesExactlyTheWindowSizeBack(int distance, bool expectMatch)
    {
        byte[] original = RepeatAtDistance(distance);

        byte[] compressed = Encode(original, _codec.Encode);
        int allLiterals = _codec.MaxCompressedSize(original.Length);

        // The repeated 64 bytes cost 3 bytes as a match, or about 64 as literals. A chance
        // 4-byte match in the random filler saves at most a byte, so 32 separates the cases.
        Assert.Equal(expectMatch, compressed.Length < allLiterals - 32);
        Assert.Equal(original, Decode(compressed, original.Length));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(256)]
    [InlineData(257)]
    public void HandlesLiteralRunsAroundTheGroupBoundary(int length)
    {
        byte[] original = TestData.Random(length);
        byte[] compressed = Encode(original, _codec.Encode);

        Assert.Equal((length + 127) / 128 + length, compressed.Length);
        Assert.Equal(original, Decode(compressed, length));
    }

    [Theory]
    [InlineData(LzssCodec.MaxMatch - 1)]
    [InlineData(LzssCodec.MaxMatch)]
    [InlineData(LzssCodec.MaxMatch + 1)]
    [InlineData(LzssCodec.MaxMatch + LzssCodec.MinMatch)]
    public void HandlesMatchesAroundTheMaximumLength(int runLength)
    {
        byte[] original = [.. TestData.Random(16, seed: 5), .. TestData.Constant(runLength), .. TestData.Random(16, seed: 6)];

        Assert.Equal(original, Decode(Encode(original, _codec.Encode), original.Length));
    }

    [Fact]
    public void ReportsFailureInsteadOfOverrunningTheBuffer()
    {
        byte[] original = TestData.Mixed(4096);

        for (int size = 0; size < 64; size++)
            Assert.Equal(-1, _codec.Encode(original, new byte[size]));
    }

    [Fact]
    public void RejectsAMatchBeforeTheStartOfTheBlock()
    {
        // One literal, then a match reaching 2 bytes back.
        byte[] malformed = [0x00, 0x41, 0x80, 0x01, 0x00];

        Assert.Throws<InvalidDataException>(() => _codec.Decode(malformed, new byte[64]));
    }

    [Fact]
    public void RejectsADistanceLargerThanTheWindow()
    {
        // 0x8000 encodes distance 32769, one past the window, so it is rejected before the start check.
        byte[] malformed = [0x00, 0x41, 0x80, 0x00, 0x80];

        var exception = Assert.Throws<InvalidDataException>(() => _codec.Decode(malformed, new byte[64]));
        Assert.Contains("window", exception.Message);
    }

    [Fact]
    public void RejectsATruncatedMatch()
    {
        byte[] malformed = [0x00, 0x41, 0x80, 0x00];

        Assert.Throws<InvalidDataException>(() => _codec.Decode(malformed, new byte[64]));
    }

    [Fact]
    public void RejectsATruncatedLiteralRun()
    {
        byte[] malformed = [0x05, 0x01, 0x02];

        Assert.Throws<InvalidDataException>(() => _codec.Decode(malformed, new byte[64]));
    }

    [Fact]
    public void RejectsAStreamThatExpandsPastTheBlockSize()
    {
        // One literal, then a 131-byte match into a 16-byte destination.
        byte[] malformed = [0x00, 0x41, 0xFF, 0x00, 0x00];

        Assert.Throws<InvalidDataException>(() => _codec.Decode(malformed, new byte[16]));
    }

    [Fact]
    public void TruncationEitherThrowsOrDecodesAShorterPrefix()
    {
        byte[] original = TestData.Mixed(4000);
        byte[] compressed = Encode(original, _codec.Encode);
        byte[] restored = new byte[original.Length];

        for (int length = 0; length < compressed.Length; length++)
        {
            int written;
            try
            {
                written = _codec.Decode(compressed.AsSpan(0, length), restored);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            // Cut on a token boundary: there is no length header, so the decoder can only
            // stop early. The container catches it by comparing with the block size.
            Assert.True(written < original.Length);
            Assert.Equal(original.AsSpan(0, written).ToArray(), restored.AsSpan(0, written).ToArray());
        }
    }

    [Fact]
    public void CorruptStreamsOnlyEverRaiseInvalidData()
    {
        byte[] original = TestData.Mixed(4000);
        byte[] compressed = Encode(original, _codec.Encode);
        var random = new Random(31);

        for (int i = 0; i < 2000; i++)
        {
            byte[] corrupt = (byte[])compressed.Clone();
            corrupt[random.Next(corrupt.Length)] ^= (byte)(1 << random.Next(8));

            try
            {
                _codec.Decode(corrupt, new byte[original.Length]);
            }
            catch (InvalidDataException)
            {
            }
        }
    }

    private delegate int Encoder(ReadOnlySpan<byte> source, Span<byte> destination);

    private byte[] Encode(byte[] original, Encoder encode)
    {
        byte[] buffer = new byte[_codec.MaxCompressedSize(original.Length)];
        int length = encode(original, buffer);

        Assert.True(length >= 0, "MaxCompressedSize was too small.");
        return buffer.AsSpan(0, length).ToArray();
    }

    private byte[] Decode(byte[] compressed, int originalLength)
    {
        byte[] restored = new byte[originalLength];

        Assert.Equal(originalLength, _codec.Decode(compressed, restored));
        return restored;
    }

    /// <summary>64 random bytes, random filler, then the same 64 bytes <paramref name="distance"/> later.</summary>
    private static byte[] RepeatAtDistance(int distance)
    {
        byte[] block = TestData.Random(64, seed: 11);
        byte[] filler = TestData.Random(distance - block.Length, seed: 12);

        return [.. block, .. filler, .. block];
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
        "far-repeat" => RepeatAtDistance(LzssCodec.WindowSize - 100),
        "window-edge" => RepeatAtDistance(LzssCodec.WindowSize),
        "past-window" => RepeatAtDistance(LzssCodec.WindowSize + 1),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown sample."),
    };
}
