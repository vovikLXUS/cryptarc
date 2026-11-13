using CryptArc.App.Localization;
using CryptArc.App.Services;
using CryptArc.Core;
using CryptArc.Core.Analysis;

namespace CryptArc.App.ViewModels;

/// <summary>
/// Whole-file figures for the summary cards, aggregated over the profiled blocks.
/// </summary>
/// <remarks>
/// <see cref="DataAnalyzer.Analyze"/> samples only the first
/// <see cref="DataAnalyzer.SampleSize"/> bytes, so its profile describes the start of
/// the file, not the file. The container picks a codec per block (ADR-0007), so the
/// honest summary is the length-weighted mean of the block profiles and the codec
/// chosen for the most blocks.
/// </remarks>
/// <param name="Entropy">Mean entropy in bits per byte, weighted by block length.</param>
/// <param name="RepeatRatio">Mean repeat ratio, weighted by block length.</param>
/// <param name="TextRatio">Mean text share, weighted by block length.</param>
/// <param name="RecommendedName">Name of the most-picked codec, or null if it is not implemented.</param>
/// <param name="RecommendedDisplay">The most-picked codec as shown to the user.</param>
/// <param name="RecommendedBlocks">How many blocks that codec was picked for.</param>
/// <param name="BlockCount">How many blocks were profiled.</param>
/// <param name="RunnerUp">The second most-picked codec, or null if every block got the same one.</param>
public sealed record FileSummary(
    double Entropy,
    double RepeatRatio,
    double TextRatio,
    string? RecommendedName,
    string RecommendedDisplay,
    int RecommendedBlocks,
    int BlockCount,
    string? RunnerUp = null)
{
    /// <summary>Aggregates the block profiles of a measurement.</summary>
    /// <param name="report">The measurement of the loaded file.</param>
    public static FileSummary From(MeasurementReport report)
    {
        BlockProfile[] blocks = report.Blocks;

        if (blocks.Length == 0)
        {
            DataProfile p = report.Profile;
            CodecId only = DataAnalyzer.Recommend(p);
            return new FileSummary(p.Entropy, p.RepeatRatio, p.TextRatio,
                NameOf(only), Display(only), 0, 0);
        }

        double entropy = 0, repeat = 0, text = 0, total = 0;
        var votes = new int[256];

        foreach (BlockProfile block in blocks)
        {
            entropy += block.Profile.Entropy * block.Length;
            repeat += block.Profile.RepeatRatio * block.Length;
            text += block.Profile.TextRatio * block.Length;
            total += block.Length;
            votes[(byte)DataAnalyzer.Recommend(block.Profile)]++;
        }

        int winner = 0, second = -1;
        for (int i = 1; i < votes.Length; i++)
        {
            if (votes[i] > votes[winner])
            {
                second = winner;
                winner = i;
            }
            else if (votes[i] > 0 && (second < 0 || votes[i] > votes[second]))
            {
                second = i;
            }
        }

        string? runnerUp = second >= 0 && votes[second] > 0 ? Display((CodecId)second) : null;

        var codec = (CodecId)winner;
        return new FileSummary(entropy / total, repeat / total, text / total,
            NameOf(codec), Display(codec), votes[winner], blocks.Length, runnerUp);
    }

    private static string? NameOf(CodecId id) =>
        CodecRegistry.TryGet(id, out ICodec? codec) ? codec.Name : null;

    private static string Display(CodecId id) => NameOf(id) ?? Localizer.Instance.Format("Analyse.NotImplemented", id);
}
