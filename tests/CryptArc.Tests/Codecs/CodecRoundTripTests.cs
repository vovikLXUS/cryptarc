using CryptArc.Core;

namespace CryptArc.Tests.Codecs;

/// <summary>
/// The contract every codec must satisfy: Decode(Encode(x)) == x, for any input.
/// Runs against the whole registry, so a newly registered algorithm is covered
/// the moment it is added.
/// </summary>
public class CodecRoundTripTests
{
    public static TheoryData<CodecId, string> Cases()
    {
        var data = new TheoryData<CodecId, string>();

        foreach (ICodec codec in CodecRegistry.All)
        {
            foreach (string sample in new[] { "empty", "single", "constant", "alternating", "random", "mixed", "text" })
                data.Add(codec.Id, sample);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RoundTripsExactly(CodecId id, string sample)
    {
        ICodec codec = CodecRegistry.Get(id);
        byte[] original = Sample(sample);

        byte[] compressed = new byte[codec.MaxCompressedSize(original.Length)];
        int compressedLength = codec.Encode(original, compressed);

        Assert.True(compressedLength >= 0,
            $"{codec.Name}: MaxCompressedSize({original.Length}) was too small for '{sample}'.");

        byte[] restored = new byte[original.Length];
        int restoredLength = codec.Decode(compressed.AsSpan(0, compressedLength), restored);

        Assert.Equal(original.Length, restoredLength);
        Assert.Equal(original, restored);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void NeverExceedsTheDeclaredUpperBound(CodecId id, string sample)
    {
        ICodec codec = CodecRegistry.Get(id);
        byte[] original = Sample(sample);

        byte[] compressed = new byte[codec.MaxCompressedSize(original.Length)];

        Assert.True(codec.Encode(original, compressed) <= compressed.Length);
    }

    [Fact]
    public void ReportsFailureInsteadOfOverrunningTheBuffer()
    {
        ICodec codec = CodecRegistry.Get(CodecId.Rle);
        byte[] original = TestData.Random(4096);

        // Deliberately far too small: the codec must return -1, not throw or corrupt memory.
        Assert.Equal(-1, codec.Encode(original, new byte[8]));
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
