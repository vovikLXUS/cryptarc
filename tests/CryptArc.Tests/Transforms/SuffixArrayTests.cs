using CryptArc.Core.Transforms;

namespace CryptArc.Tests.Transforms;

public class SuffixArrayTests
{
    public static TheoryData<string, int> ShapesAndSizes()
    {
        var data = new TheoryData<string, int>();

        foreach (string shape in new[] { "random", "constant", "alternating", "mixed", "text" })
        {
            foreach (int size in new[] { 0, 1, 2, 3, 7, 64, 255, 256, 257, 1000, 4096 })
                data.Add(shape, size);
        }

        return data;
    }

    [Fact]
    public void SortsTheTextbookExample()
    {
        int[] sa = new int[6];

        SuffixArray.Build("banana"u8, sa);

        // a, ana, anana, banana, na, nana
        Assert.Equal([5, 3, 1, 0, 4, 2], sa);
    }

    [Fact]
    public void ShorterSuffixSortsBeforeTheLongerOneItPrefixes()
    {
        int[] sa = new int[4];

        SuffixArray.Build("aaaa"u8, sa);

        Assert.Equal([3, 2, 1, 0], sa);
    }

    [Theory]
    [MemberData(nameof(ShapesAndSizes))]
    public void PrefixDoublingAgreesWithTheNaiveSort(string shape, int size)
    {
        byte[] text = Sample(shape, size);
        int[] expected = new int[size];
        int[] actual = new int[size];

        SuffixArray.BuildNaive(text, expected);
        SuffixArray.Build(text, actual);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void WritesOnlyTheFirstNEntries()
    {
        int[] sa = [-1, -1, -1, -1, -1];

        SuffixArray.Build("aba"u8, sa);

        Assert.Equal([2, 0, 1, -1, -1], sa);
    }

    private static byte[] Sample(string shape, int size) => shape switch
    {
        "random" => TestData.Random(size),
        "constant" => TestData.Constant(size),
        "alternating" => TestData.Alternating(size),
        "mixed" => TestData.Mixed(size),
        "text" => TestData.Text().AsSpan(0, size).ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown shape."),
    };
}
