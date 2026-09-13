using CryptArc.Core;
using CryptArc.Core.Analysis;

namespace CryptArc.Tests;

public class DataAnalyzerTests
{
    [Fact]
    public void ConstantDataHasZeroEntropy()
    {
        DataProfile profile = DataAnalyzer.Analyze(TestData.Constant(4096));

        Assert.Equal(0, profile.Entropy, precision: 6);
        Assert.True(profile.RepeatRatio > 0.99);
        Assert.Equal(CodecId.Rle, DataAnalyzer.Recommend(profile));
    }

    [Fact]
    public void RandomDataIsRecognisedAsIncompressible()
    {
        DataProfile profile = DataAnalyzer.Analyze(TestData.Random(64 * 1024));

        Assert.True(profile.Entropy > 7.9, $"Entropy was {profile.Entropy:F3}, expected close to 8.");
        Assert.Equal(CodecId.Store, DataAnalyzer.Recommend(profile));
    }

    [Fact]
    public void EnglishTextIsRoutedToTheBlockSortingCodec()
    {
        DataProfile profile = DataAnalyzer.Analyze(TestData.Text());

        Assert.True(profile.TextRatio > 0.99);
        Assert.InRange(profile.Entropy, 2.0, 6.0);
        Assert.Equal(CodecId.Bwt, DataAnalyzer.Recommend(profile));
    }

    [Fact]
    public void EmptyInputIsStored()
    {
        DataProfile profile = DataAnalyzer.Analyze([]);

        Assert.Equal(0, profile.SampledBytes);
        Assert.Equal(CodecId.Store, DataAnalyzer.Recommend(profile));
    }

    [Fact]
    public void SamplingIsCappedSoLargeBlocksStayCheap()
    {
        DataProfile profile = DataAnalyzer.Analyze(TestData.Random(5 * 1024 * 1024));

        Assert.Equal(DataAnalyzer.SampleSize, profile.SampledBytes);
    }
}
