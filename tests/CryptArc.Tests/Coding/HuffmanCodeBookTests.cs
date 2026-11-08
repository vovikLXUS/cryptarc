using CryptArc.Core.Coding;

namespace CryptArc.Tests.Coding;

public class HuffmanCodeBookTests
{
    private static (int[] Frequencies, byte[] Lengths, uint[] Codes, long Bits) Build(byte[] data)
    {
        var frequencies = new int[256];
        var lengths = new byte[256];
        var codes = new uint[256];
        long bits = HuffmanCodeBook.Build(data, frequencies, lengths, codes);
        return (frequencies, lengths, codes, bits);
    }

    [Fact]
    public void EmptyInputHasNoCodes()
    {
        var (frequencies, lengths, codes, bits) = Build([]);

        Assert.Equal(0, bits);
        Assert.All(frequencies, f => Assert.Equal(0, f));
        Assert.All(lengths, l => Assert.Equal(0, l));
        Assert.All(codes, c => Assert.Equal(0u, c));
    }

    [Fact]
    public void ASingleSymbolGetsAOneBitCode()
    {
        var (frequencies, lengths, _, bits) = Build(TestData.Constant(10, 0x41));

        Assert.Equal(10, frequencies[0x41]);
        Assert.Equal(1, lengths[0x41]);
        Assert.Equal(10, bits);
    }

    [Theory]
    [InlineData("abracadabra")]
    [InlineData("the quick brown fox jumps over the lazy dog")]
    public void CodesArePrefixFreeAndComplete(string text)
    {
        var (_, lengths, codes, _) = Build(System.Text.Encoding.ASCII.GetBytes(text));

        var used = Enumerable.Range(0, 256).Where(s => lengths[s] > 0).ToArray();

        // Kraft equality: a full binary tree uses the whole code space.
        double kraft = used.Sum(s => Math.Pow(2, -lengths[s]));
        Assert.Equal(1.0, kraft, precision: 12);

        // No code is a prefix of another (codes are most significant bit first).
        foreach (int a in used)
        foreach (int b in used)
        {
            if (a == b || lengths[a] > lengths[b])
                continue;

            uint prefixOfB = codes[b] >> (lengths[b] - lengths[a]);
            Assert.False(prefixOfB == codes[a], $"Code of {a} is a prefix of the code of {b}.");
        }
    }

    [Fact]
    public void MoreFrequentSymbolsNeverGetLongerCodes()
    {
        byte[] data = TestData.Mixed(4096);
        var (frequencies, lengths, _, _) = Build(data);

        for (int a = 0; a < 256; a++)
        for (int b = 0; b < 256; b++)
        {
            if (frequencies[a] > frequencies[b] && frequencies[b] > 0)
                Assert.True(lengths[a] <= lengths[b], $"Symbol {a} is more frequent than {b} but has a longer code.");
        }
    }

    [Fact]
    public void BitCountIsTheSumOfFrequencyTimesLength()
    {
        byte[] data = TestData.Text();
        var (frequencies, lengths, _, bits) = Build(data);

        long expected = Enumerable.Range(0, 256).Sum(s => (long)frequencies[s] * lengths[s]);
        Assert.Equal(expected, bits);
        Assert.True(bits < data.Length * 8L);
    }

    [Fact]
    public void ShortOutputSpansAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HuffmanCodeBook.Build([1, 2, 3], new int[255], new byte[256], new uint[256]));
    }
}
