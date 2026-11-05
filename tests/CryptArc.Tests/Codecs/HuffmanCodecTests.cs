using CryptArc.Core.Codecs;

namespace CryptArc.Tests.Codecs;

public class HuffmanCodecTests
{
    private const int HeaderSize = 132;
    private const int MaxCodeLength = 15;

    private readonly HuffmanCodec _codec = new();

    public static TheoryData<string> Decoders() => new() { "table", "tree" };

    public static TheoryData<string, int> DecodersAndFibonacciSizes()
    {
        var data = new TheoryData<string, int>();

        // 18 and more symbols need limiting; 12 and more use the second table level.
        foreach (string decoder in new[] { "table", "tree" })
        {
            foreach (int symbols in new[] { 2, 3, 11, 12, 13, 16, 17, 18, 24 })
                data.Add(decoder, symbols);
        }

        return data;
    }

    private byte[] Encode(byte[] original)
    {
        byte[] compressed = new byte[_codec.MaxCompressedSize(original.Length)];
        int length = _codec.Encode(original, compressed);

        Assert.True(length >= 0);
        return compressed[..length];
    }

    private int Decode(string decoder, byte[] source, byte[] destination) => decoder switch
    {
        "table" => _codec.Decode(source, destination),
        "tree" => HuffmanCodec.DecodeTreeWalk(source, destination),
        _ => throw new ArgumentOutOfRangeException(nameof(decoder), decoder, "Unknown decoder."),
    };

    /// <summary>Symbol i occurs Fib(i) times: the input that makes the deepest possible tree.</summary>
    private static byte[] Fibonacci(int symbols)
    {
        var output = new List<byte>();
        int previous = 0;
        int current = 1;

        for (int symbol = 0; symbol < symbols; symbol++)
        {
            output.AddRange(Enumerable.Repeat((byte)symbol, current));
            (previous, current) = (current, previous + current);
        }

        return output.ToArray();
    }

    /// <summary>
    /// All 256 byte values: 246 occur once and merge into a subtree of weight 246 and
    /// depth 8. The other 10 continue a Fibonacci chain from that weight, which puts the
    /// subtree 10 levels down and its leaves at depth 18, past the 15-bit limit.
    /// </summary>
    private static byte[] AllSymbolsSkewed()
    {
        const int rareSymbols = 246;

        var output = new List<byte>(Enumerable.Range(0, rareSymbols).Select(symbol => (byte)symbol));
        int previous = rareSymbols;
        int current = rareSymbols;

        for (int symbol = rareSymbols; symbol < 256; symbol++)
        {
            output.AddRange(Enumerable.Repeat((byte)symbol, current));
            (previous, current) = (current, previous + current);
        }

        return output.ToArray();
    }

    private static int[] ReadLengths(byte[] stream)
    {
        int[] lengths = new int[256];

        for (int i = 0; i < 128; i++)
        {
            lengths[2 * i] = stream[4 + i] & 0x0F;
            lengths[2 * i + 1] = stream[4 + i] >> 4;
        }

        return lengths;
    }

    /// <summary>A header declaring <paramref name="count"/> symbols, with no code lengths set yet.</summary>
    private static byte[] Header(uint count, int payloadBytes = 16)
    {
        byte[] stream = new byte[HeaderSize + payloadBytes];
        BitConverter.TryWriteBytes(stream, count);
        return stream;
    }

    /// <summary>Package-merge over plain lists: the total bit cost of the optimal limited code.</summary>
    private static long OptimalLimitedCost(long[] weights, int limit)
    {
        List<long> items = [];

        for (int level = 0; level < limit; level++)
        {
            List<long> packages = Enumerable.Range(0, items.Count / 2)
                .Select(i => items[2 * i] + items[2 * i + 1])
                .ToList();

            items = weights.Concat(packages).OrderBy(weight => weight).ToList();
        }

        return items.Take(2 * weights.Length - 2).Sum();
    }

    [Fact]
    public void ProducesTheDocumentedStream()
    {
        byte[] stream = Encode("aaaabbc"u8.ToArray());

        // a = 0, b = 10, c = 11 -> first bits on the wire: 0000 10 10 11.
        byte[] expected = new byte[HeaderSize + 2];
        expected[0] = 7;
        expected[4 + 'a' / 2] = 1 << 4;
        expected[4 + 'b' / 2] = 2 | 2 << 4; // 'b' and 'c' share a byte.
        expected[HeaderSize] = 0x50;
        expected[HeaderSize + 1] = 0x03;

        Assert.Equal(expected, stream);
    }

    [Fact]
    public void EncodesEmptyInputAsNothing()
    {
        Assert.Equal(0, _codec.MaxCompressedSize(0));
        Assert.Equal(0, _codec.Encode([], []));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(8, 1)]
    [InlineData(9, 2)]
    [InlineData(5000, 625)]
    public void GivesASingleDistinctSymbolOneBit(int length, int payloadBytes)
    {
        byte[] stream = Encode(TestData.Constant(length));

        Assert.Equal(HeaderSize + payloadBytes, stream.Length);
    }

    [Fact]
    public void ShrinksSkewedText()
    {
        byte[] original = TestData.Text();

        Assert.True(Encode(original).Length < original.Length * 0.7);
    }

    [Fact]
    public void OnlyTheHeaderIsOverheadOnFlatInput()
    {
        // Every byte value exactly equally often: the optimal code is a flat 8 bits.
        byte[] original = Enumerable.Range(0, 256 * 4).Select(i => (byte)i).ToArray();

        Assert.Equal(original.Length + HeaderSize, Encode(original).Length);
    }

    [Theory]
    [InlineData(18)]
    [InlineData(24)]
    public void LimitsDeepTreesToTheOptimalLimitedCode(int symbols)
    {
        byte[] original = Fibonacci(symbols);
        byte[] stream = Encode(original);
        int[] lengths = ReadLengths(stream);

        long[] counts = new long[256];
        foreach (byte value in original)
            counts[value]++;

        long kraftSum = lengths.Where(length => length > 0).Sum(length => 1L << (MaxCodeLength - length));
        long cost = Enumerable.Range(0, 256).Sum(symbol => counts[symbol] * lengths[symbol]);
        long[] weights = counts.Where(count => count > 0).ToArray();

        Assert.Equal(MaxCodeLength, lengths.Max());
        Assert.Equal(1L << MaxCodeLength, kraftSum);
        Assert.Equal(OptimalLimitedCost(weights, MaxCodeLength), cost);
        Assert.Equal(HeaderSize + (cost + 7) / 8, stream.Length);
    }

    [Fact]
    public void LimitsACodeThatUsesEverySymbol()
    {
        byte[] original = AllSymbolsSkewed();
        byte[] stream = Encode(original);
        int[] lengths = ReadLengths(stream);

        long[] counts = new long[256];
        foreach (byte value in original)
            counts[value]++;

        long kraftSum = lengths.Sum(length => 1L << (MaxCodeLength - length));
        long cost = Enumerable.Range(0, 256).Sum(symbol => counts[symbol] * lengths[symbol]);

        Assert.DoesNotContain(0, lengths);
        Assert.Equal(MaxCodeLength, lengths.Max());
        Assert.Equal(1L << MaxCodeLength, kraftSum);
        Assert.Equal(OptimalLimitedCost(counts, MaxCodeLength), cost);

        // A more generous limit does strictly better, so this one really was binding.
        Assert.True(OptimalLimitedCost(counts, 32) < cost);
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void RoundTripsALimitedCodeThatUsesEverySymbol(string decoder)
    {
        byte[] original = AllSymbolsSkewed();
        byte[] restored = new byte[original.Length];

        Assert.Equal(original.Length, Decode(decoder, Encode(original), restored));
        Assert.Equal(original, restored);
    }

    [Theory]
    [MemberData(nameof(DecodersAndFibonacciSizes))]
    public void RoundTripsEveryCodeLength(string decoder, int symbols)
    {
        byte[] original = Fibonacci(symbols);
        byte[] restored = new byte[original.Length];

        Assert.Equal(original.Length, Decode(decoder, Encode(original), restored));
        Assert.Equal(original, restored);
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void RoundTripsAllByteValues(string decoder)
    {
        byte[] original = TestData.Random(100_000);
        byte[] restored = new byte[original.Length];

        Assert.Equal(original.Length, Decode(decoder, Encode(original), restored));
        Assert.Equal(original, restored);
    }

    [Fact]
    public void TreeWalkAgreesWithTheLookupTable()
    {
        byte[][] samples =
        [
            [0x7F],
            TestData.Constant(5000),
            TestData.Alternating(5000),
            TestData.Random(5000),
            TestData.Mixed(20000),
            TestData.Text(),
            Fibonacci(24),
        ];

        foreach (byte[] original in samples)
        {
            byte[] stream = Encode(original);
            byte[] fromTable = new byte[original.Length];
            byte[] fromTree = new byte[original.Length];

            Assert.Equal(_codec.Decode(stream, fromTable), HuffmanCodec.DecodeTreeWalk(stream, fromTree));
            Assert.Equal(fromTree, fromTable);
        }
    }

    [Fact]
    public void ReportsFailureAtTheExactBufferSize()
    {
        byte[] original = TestData.Mixed(20000);
        int needed = Encode(original).Length;

        Assert.Equal(needed, _codec.Encode(original, new byte[needed]));
        Assert.Equal(-1, _codec.Encode(original, new byte[needed - 1]));
        Assert.Equal(-1, _codec.Encode(original, new byte[HeaderSize]));
        Assert.Equal(-1, _codec.Encode(original, new byte[HeaderSize - 1]));
        Assert.Equal(-1, _codec.Encode(original, []));
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void RejectsEveryTruncation(string decoder)
    {
        byte[] original = TestData.Text()[..300];
        byte[] stream = Encode(original);
        byte[] restored = new byte[original.Length];

        for (int length = 1; length < stream.Length; length++)
        {
            byte[] truncated = stream[..length];

            Assert.Throws<InvalidDataException>(() => Decode(decoder, truncated, restored));
        }
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void RejectsZeroDeclaredSymbols(string decoder)
    {
        byte[] stream = Encode("abc"u8.ToArray());
        BitConverter.TryWriteBytes(stream, 0u);

        Assert.Throws<InvalidDataException>(() => Decode(decoder, stream, new byte[3]));
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void RejectsStreamThatExpandsPastTheBlockSize(string decoder)
    {
        byte[] stream = Encode(TestData.Text());

        Assert.Throws<InvalidDataException>(() => Decode(decoder, stream, new byte[16]));
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void RejectsMoreSymbolsThanThePayloadCanHold(string decoder)
    {
        byte[] stream = Header(count: 1000, payloadBytes: 1);
        stream[4] = 1 | 1 << 4; // Two valid 1-bit codes; 8 payload bits cannot make 1000 symbols.

        Assert.Throws<InvalidDataException>(() => Decode(decoder, stream, new byte[1000]));
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void RejectsAnOverSubscribedCode(string decoder)
    {
        byte[] stream = Header(count: 8);
        stream.AsSpan(4, 128).Fill(1 | 1 << 4); // 256 codes of 1 bit each.

        Assert.Throws<InvalidDataException>(() => Decode(decoder, stream, new byte[8]));
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void RejectsAnIncompleteCode(string decoder)
    {
        byte[] stream = Header(count: 8);
        stream[4] = 2 | 2 << 4; // Two codes of 2 bits leave half the code space unused.

        Assert.Throws<InvalidDataException>(() => Decode(decoder, stream, new byte[8]));
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void RejectsAHeaderWithNoCodes(string decoder)
    {
        Assert.Throws<InvalidDataException>(() => Decode(decoder, Header(count: 8), new byte[8]));
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void RejectsASingleSymbolWithALongerCode(string decoder)
    {
        byte[] stream = Header(count: 8);
        stream[4] = 2;

        Assert.Throws<InvalidDataException>(() => Decode(decoder, stream, new byte[8]));
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void RejectsTheUnusedBitOfASingleSymbolCode(string decoder)
    {
        byte[] stream = Header(count: 1, payloadBytes: 1);
        stream[4] = 1;
        stream[HeaderSize] = 0x01; // The only valid code is 0.

        Assert.Throws<InvalidDataException>(() => Decode(decoder, stream, new byte[1]));
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void SurvivesCorruptedStreams(string decoder)
    {
        byte[] original = TestData.Mixed(4000);
        byte[] stream = Encode(original);
        byte[] restored = new byte[original.Length];
        var random = new Random(31);

        for (int round = 0; round < 2000; round++)
        {
            byte[] corrupted = (byte[])stream.Clone();

            // Half the rounds aim at the header, where the lengths and the count live.
            int range = round % 2 == 0 ? HeaderSize : corrupted.Length;

            for (int i = 0; i < 4; i++)
                corrupted[random.Next(range)] = (byte)random.Next(256);

            try
            {
                Decode(decoder, corrupted, restored);
            }
            catch (InvalidDataException)
            {
                // Rejecting it is fine; any other exception fails the test.
            }
        }
    }
}
