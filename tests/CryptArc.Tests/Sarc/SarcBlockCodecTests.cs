using CryptArc.Core.Sarc;
using static CryptArc.Tests.Sarc.SarcTestKit;

namespace CryptArc.Tests.Sarc;

/// <summary>Behaviour of the block coding of §3 that the round trip alone does not show.</summary>
public class SarcBlockCodecTests
{
    // Canonical codes: ref (256) = 0, a = 10, b = 11; LEN and DIST are lone symbols with code 0.
    // "a", "b", then a reference of length 4 at distance 2 → "ababab".
    private static readonly byte[] TwoLiterals = Table((97, 2), (98, 2), (256, 1));
    private static readonly byte[] OneLength = Table((1, 1));      // length 4
    private static readonly byte[] DistanceTwo = Table((1, 1));    // code 1 → distance 2

    private static SarcErrorCode DecodeFailure(byte[] block, int rawLength) =>
        Assert.Throws<SarcException>(() => SarcBlockCodec.Decode(block, new byte[rawLength])).Code;

    [Fact]
    public void AHandBuiltBlockDecodes()
    {
        // a=10 b=11 ref=0 len=0 dist=0, then padding: 1011_0000.
        byte[] block = Block(TwoLiterals, OneLength, DistanceTwo, 3, [0b1011_0000]);
        byte[] output = new byte[6];

        SarcBlockCodec.Decode(block, output);

        Assert.Equal("ababab"u8.ToArray(), output);
    }

    [Fact]
    public void BitsAreWrittenMostSignificantFirst()
    {
        Span<byte> buffer = stackalloc byte[2];
        var writer = new MsbBitWriter(buffer);
        writer.Write(1, 1);
        writer.Write(0b011, 3);
        writer.Write(0b1_0000_1, 6);

        Assert.Equal(2, writer.Flush());
        Assert.Equal(0b1011_1000, buffer[0]);
        Assert.Equal(0b0100_0000, buffer[1]);
    }

    [Fact]
    public void ADistanceSymbolOfSixteenIsCode() =>
        Assert.Equal(SarcErrorCode.Code, DecodeFailure(Block(TwoLiterals, OneLength, Table((16, 1)), 0, []), 0));

    [Fact]
    public void ALitSymbolAbove256IsCode() =>
        Assert.Equal(SarcErrorCode.Code, DecodeFailure(Block(Table((257, 1)), Table(), Table(), 0, []), 0));

    [Fact]
    public void ACodeLongerThanFifteenBitsIsCode() =>
        Assert.Equal(SarcErrorCode.Code, DecodeFailure(Block(Table((1, 1), (2, 16)), Table(), Table(), 0, []), 0));

    [Fact]
    public void AZeroLengthEntryIsCode() =>
        Assert.Equal(SarcErrorCode.Code, DecodeFailure(Block(Table((1, 1), (2, 0)), Table(), Table(), 0, []), 0));

    [Fact]
    public void BrokenKraftEqualityIsCode() =>
        Assert.Equal(SarcErrorCode.Code, DecodeFailure(Block(Table((1, 1), (2, 2)), Table(), Table(), 0, []), 0));

    [Fact]
    public void AnOversubscribedCodeIsCode() =>
        Assert.Equal(SarcErrorCode.Code, DecodeFailure(Block(Table((1, 1), (2, 1), (3, 1)), Table(), Table(), 0, []), 0));

    [Fact]
    public void SymbolsOutOfOrderAreCode() =>
        Assert.Equal(SarcErrorCode.Code, DecodeFailure(Block(Table((2, 1), (1, 1)), Table(), Table(), 0, []), 0));

    [Fact]
    public void ALoneSymbolWithLengthTwoIsCode() =>
        Assert.Equal(SarcErrorCode.Code, DecodeFailure(Block(Table((65, 2)), Table(), Table(), 1, [0]), 1));

    [Fact]
    public void AReferenceBeforeTheStartIsCode() =>
        // A reference first: 0 0 0 → distance 2 with nothing decoded.
        Assert.Equal(SarcErrorCode.Code, DecodeFailure(Block(TwoLiterals, OneLength, DistanceTwo, 1, [0b0000_0000]), 4));

    [Fact]
    public void AReferencePastTheEndIsCode() =>
        Assert.Equal(SarcErrorCode.Code, DecodeFailure(Block(TwoLiterals, OneLength, DistanceTwo, 3, [0b1011_0000]), 5));

    [Fact]
    public void AReferenceWithAnEmptyLengthTableIsCode() =>
        Assert.Equal(SarcErrorCode.Code, DecodeFailure(Block(TwoLiterals, Table(), DistanceTwo, 3, [0b1011_0000]), 6));

    [Fact]
    public void NonZeroPaddingIsCode() =>
        Assert.Equal(SarcErrorCode.Code, DecodeFailure(Block(TwoLiterals, OneLength, DistanceTwo, 3, [0b1011_0001]), 6));

    [Fact]
    public void AStreamThatEndsInsideAnElementIsCode() =>
        Assert.Equal(SarcErrorCode.Code, DecodeFailure(Block(TwoLiterals, OneLength, DistanceTwo, 4, [0b1011_0000]), 7));

    [Fact]
    public void BytesAfterTheStreamArePayload() =>
        Assert.Equal(SarcErrorCode.Payload, DecodeFailure([.. Block(TwoLiterals, OneLength, DistanceTwo, 3, [0b1011_0000]), 0], 6));

    [Fact]
    public void AStreamLengthPastTheBlockIsPayload() =>
        Assert.Equal(SarcErrorCode.Payload, DecodeFailure(Block(TwoLiterals, OneLength, DistanceTwo, 3, [0b1011_0000], streamLength: 2), 6));

    [Fact]
    public void UnusedStreamBytesArePayload() =>
        Assert.Equal(SarcErrorCode.Payload, DecodeFailure(Block(TwoLiterals, OneLength, DistanceTwo, 3, [0b1011_0000, 0]), 6));

    [Fact]
    public void FewerBytesThanRawLengthArePayload() =>
        Assert.Equal(SarcErrorCode.Payload, DecodeFailure(Block(TwoLiterals, OneLength, DistanceTwo, 3, [0b1011_0000]), 7));

    [Fact]
    public void ATableRunningPastTheBlockIsPayload() =>
        Assert.Equal(SarcErrorCode.Payload, DecodeFailure(TwoLiterals[..^1], 0));

    [Fact]
    public void AnEmptyBlockIsFourteenBytes()
    {
        byte[] coded = SarcBlockCodec.Encode([]);
        Assert.Equal(14, coded.Length);
        SarcBlockCodec.Decode(coded, []);
    }

    [Fact]
    public void ALoneLiteralGetsTheOneBitCodeZero()
    {
        byte[] coded = SarcBlockCodec.Encode([7]);
        Assert.Equal([.. Table((7, 1)), .. Table(), .. Table(), .. FromHex("00000001 00000001 00")], coded);
    }

    [Fact]
    public void HashChainsAndLinearScanFindTheSameTokensWithAnUnboundedChain()
    {
        byte[] data = TestData.Mixed(12_000);
        foreach (SarcParse parse in new[] { SarcParse.Greedy, SarcParse.Lazy })
        {
            Assert.Equal(
                SarcBlockCodec.Encode(data, parse, linearScan: true),
                SarcBlockCodec.Encode(data, parse, linearScan: false, maxChain: int.MaxValue));
        }
    }

    [Fact]
    public void LazyMatchingIsNeverLargerOnText()
    {
        byte[] text = [.. TestData.Text(), .. System.Text.Encoding.UTF8.GetBytes("lazy lazier laziest lazily")];
        Assert.True(SarcBlockCodec.Encode(text, SarcParse.Lazy).Length <= SarcBlockCodec.Encode(text, SarcParse.Greedy).Length);
    }

    [Fact]
    public void RandomDamageToACodedBlockOnlyEverRaisesSarcException()
    {
        byte[] original = TestData.Mixed(4000);
        byte[] coded = SarcBlockCodec.Encode(original);
        var random = new Random(51);

        for (int round = 0; round < 20_000; round++)
        {
            byte[] damaged = (byte[])coded.Clone();
            switch (round % 3)
            {
                case 0:
                    damaged[random.Next(damaged.Length)] ^= (byte)(1 << random.Next(8));
                    break;
                case 1:
                    damaged = damaged[..random.Next(damaged.Length)];
                    break;
                default:
                    damaged[random.Next(Math.Min(damaged.Length, 40))] = (byte)random.Next(256);
                    break;
            }

            try
            {
                SarcBlockCodec.Decode(damaged, new byte[round % 5 == 0 ? original.Length + 3 : original.Length]);
            }
            catch (SarcException)
            {
            }
        }
    }
}
