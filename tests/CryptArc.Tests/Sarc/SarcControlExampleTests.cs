using System.Text;
using CryptArc.Core.Sarc;
using static CryptArc.Tests.Sarc.SarcTestKit;

namespace CryptArc.Tests.Sarc;

/// <summary>
/// The control example of FORMAT_SPEC_P2 §8, checked piece by piece and then as a whole:
/// 480 bytes of "hello world " in one volume, salt 00…0F, nonce prefix 01…08.
/// </summary>
public class SarcControlExampleTests
{
    /// <summary><c>ref_01_single_file.arc</c>, 172 bytes, as issued with the specification.</summary>
    private const string ReferenceVolume =
        "534152430100000001000102030405060708090a0b0c0d0e0f010203040506070800000000000000638e51d20ba4b860" +
        "bbc8d7c95e2f288c9c60bc2258dd8c9b10efb0c821d60f993f27c61c7b117447279bf77344cc0b173e4789531c90eb05" +
        "96aa90d1a631bf276b5013e6624652314ebbc1bd1f798a972607ffd7b1562c87e9b60ef303826647536fc2628df55690" +
        "14e07f49e411af265bc27006cbe3b2394a1f91b6b2838cdd2749b72e";

    private static readonly byte[] Hello = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("hello world ", 40)));

    private static readonly SarcWriterOptions ControlOptions = new()
    {
        FixedSalt = FromHex("000102030405060708090A0B0C0D0E0F"),
        FixedNoncePrefix = FromHex("0102030405060708"),
    };

    [Fact]
    public void CanonicalCodesMatchTheLitTableOfTheExample()
    {
        byte[] lengths = new byte[257];
        (int Symbol, int Length, string Code)[] expected =
        [
            (32, 3, "010"), (100, 4, "1100"), (101, 4, "1101"), (104, 4, "1110"), (108, 2, "00"),
            (111, 3, "011"), (114, 4, "1111"), (119, 3, "100"), (256, 3, "101"),
        ];
        foreach ((int symbol, int length, _) in expected)
            lengths[symbol] = (byte)length;

        uint[] codes = new uint[257];
        SarcCodeTable.AssignCodes(lengths, codes);

        foreach ((int symbol, int length, string code) in expected)
            Assert.Equal(code, Convert.ToString(codes[symbol], 2).PadLeft(length, '0'));
    }

    [Fact]
    public void TheBlockIsTheFiftySevenBytesOfTheExample()
    {
        byte[] block = SarcBlockCodec.Encode(Hello, SarcParse.Lazy);

        byte[] expected =
        [
            .. Table((32, 3), (100, 4), (101, 4), (104, 4), (108, 2), (111, 3), (114, 4), (119, 3), (256, 3)),
            .. Table((207, 1), (255, 1)),
            .. Table((4, 1)),
            .. FromHex("0000000E 00000007 ED06A3F3159D18"),
        ];

        Assert.Equal(57, block.Length);
        Assert.Equal(expected, block);
    }

    [Fact]
    public void TheExampleStreamDecodesToTheFile()
    {
        byte[] block = Block(
            Table((32, 3), (100, 4), (101, 4), (104, 4), (108, 2), (111, 3), (114, 4), (119, 3), (256, 3)),
            Table((207, 1), (255, 1)),
            Table((4, 1)),
            14, FromHex("ED06A3F3159D18"));

        byte[] output = new byte[480];
        SarcBlockCodec.Decode(block, output);

        Assert.Equal(Hello, output);
    }

    [Fact]
    public void TheWriterReproducesTheReferenceVolumeByteForByte()
    {
        byte[] volume = SarcArchiveWriter.Create([new SarcSource("hello.txt", Hello)], Password, ControlOptions).Single();

        Assert.Equal(FromHex(ReferenceVolume), volume);
    }

    [Fact]
    public void TheReferenceVolumeHasTheDocumentedHeaderAndPayload()
    {
        byte[] volume = FromHex(ReferenceVolume);

        Assert.Equal(172, volume.Length);
        Assert.Equal(FromHex("53415243 01 0000 0001 000102030405060708090A0B0C0D0E0F 010203040506070800000000 00000063"), volume[..41]);

        byte[] payload = OpenPayload(volume);
        Assert.Equal(99, payload.Length);
        Assert.Equal(
            FromHex("0001 00 00 0009 68656C6C6F2E747874 000001E0 70208846 00 01 0001 0000 00000000 000001E0 00000039 00"),
            payload[..42]);
    }

    [Fact]
    public void TheReaderRestoresTheReferenceVolume()
    {
        SarcFile file = Read([FromHex(ReferenceVolume)]).Single();

        Assert.Equal("hello.txt", file.Name);
        Assert.Equal(Hello, file.Data);
        Assert.Equal(SarcFileType.NaturalText, file.FileType);
        Assert.Equal(SarcArchiveWriter.StrategyLazy, file.Strategy);
    }

    [Fact]
    public void AWrongPasswordGivesHmacNotGarbage() =>
        Assert.Equal(SarcErrorCode.Hmac, Rejection(FromHex(ReferenceVolume), "algo2027"));
}
