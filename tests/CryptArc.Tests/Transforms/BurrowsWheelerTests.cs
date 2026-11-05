using System.Text;
using CryptArc.Core.Transforms;

namespace CryptArc.Tests.Transforms;

public class BurrowsWheelerTests
{
    public static TheoryData<int> Sizes() => new() { 0, 1, 2, 3, 100, 4096, 70_000 };

    [Fact]
    public void TransformsTheTextbookExample()
    {
        // Sorted rotations of "banana$": $banana, a$banan, ana$ban, anana$b,
        // banana$, na$bana, nana$ba. Last column "annb$aa"; the sentinel is row 4.
        byte[] column = new byte[6];

        int primaryIndex = BurrowsWheeler.Forward("banana"u8, column);

        Assert.Equal("annbaa", Encoding.ASCII.GetString(column));
        Assert.Equal(4, primaryIndex);
    }

    [Fact]
    public void InvertsTheTextbookExample()
    {
        byte[] restored = new byte[6];

        BurrowsWheeler.Inverse("annbaa"u8, 4, restored);

        Assert.Equal("banana", Encoding.ASCII.GetString(restored));
    }

    [Fact]
    public void GroupsRepeatedContextsIntoRuns()
    {
        byte[] text = TestData.Text();
        byte[] column = new byte[text.Length];

        BurrowsWheeler.Forward(text, column);

        Assert.True(CountRuns(column) * 20 < CountRuns(text),
            "The transform should leave far fewer runs than the original text.");
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void InverseRestoresEveryShape(int size)
    {
        foreach (byte[] original in new[]
                 {
                     TestData.Random(size), TestData.Constant(size),
                     TestData.Alternating(size), TestData.Mixed(size),
                 })
        {
            byte[] column = new byte[size];
            byte[] restored = new byte[size];

            int primaryIndex = BurrowsWheeler.Forward(original, column);
            BurrowsWheeler.Inverse(column, primaryIndex, restored);

            Assert.Equal(original, restored);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(-1)]
    public void RejectsPrimaryIndexOutOfRange(int primaryIndex)
    {
        Assert.Throws<InvalidDataException>(() =>
            BurrowsWheeler.Inverse("annbaa"u8, primaryIndex, new byte[6]));
    }

    [Fact]
    public void RejectsPrimaryIndexForEmptyInput()
    {
        Assert.Throws<InvalidDataException>(() => BurrowsWheeler.Inverse([], 1, []));
    }

    [Fact]
    public void RejectsColumnThatIsNotASingleCycle()
    {
        // The right primary index for this column is 4; at 1 the walk closes early.
        Assert.Throws<InvalidDataException>(() =>
            BurrowsWheeler.Inverse("annbaa"u8, 1, new byte[6]));
    }

    private static int CountRuns(ReadOnlySpan<byte> data)
    {
        int runs = data.IsEmpty ? 0 : 1;
        for (int i = 1; i < data.Length; i++)
        {
            if (data[i] != data[i - 1])
                runs++;
        }

        return runs;
    }
}
