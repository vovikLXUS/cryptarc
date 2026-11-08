using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CryptArc.App.Services;
using CryptArc.Core.Analysis;

namespace CryptArc.App.ViewModels;

/// <summary>The "Analyse" page: the file's statistics and the per-block entropy heatmap.</summary>
public partial class AnalysePageViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _hasFile;

    [ObservableProperty]
    private string _entropy = "—";

    [ObservableProperty]
    private string _repeatRatio = "—";

    [ObservableProperty]
    private string _textRatio = "—";

    [ObservableProperty]
    private string _recommendation = "—";

    [ObservableProperty]
    private string _recommendationCaption = string.Empty;

    [ObservableProperty]
    private string _blocksCaption = string.Empty;

    /// <summary>One cell per profiled block, in file order.</summary>
    public ObservableCollection<HeatmapCell> Blocks { get; } = [];

    /// <summary>Replaces the page contents with a new report. Call on the UI thread.</summary>
    /// <param name="report">The measurement of the loaded file.</param>
    /// <param name="summary">Whole-file figures aggregated from the blocks.</param>
    public void Show(MeasurementReport report, FileSummary summary)
    {
        Entropy = $"{summary.Entropy:F2}";
        RepeatRatio = $"{summary.RepeatRatio:P1}";
        TextRatio = $"{summary.TextRatio:P1}";
        Recommendation = summary.RecommendedDisplay;
        RecommendationCaption = summary.BlockCount == 0
            ? "empty file"
            : summary.RunnerUp is null
                ? $"all {summary.BlockCount} blocks"
                : $"{summary.RecommendedBlocks} of {summary.BlockCount} blocks, the rest mostly {summary.RunnerUp}";

        Blocks.Clear();
        foreach (BlockProfile block in report.Blocks)
            Blocks.Add(new HeatmapCell(block.Profile.Entropy,
                $"Offset {block.Offset:N0}\n{block.Profile.Entropy:F2} bits/byte · {block.Profile.TextRatio:P0} text\n" +
                $"Codec: {DataAnalyzer.Recommend(block.Profile)}"));

        BlocksCaption = report.Blocks.Length == CodecMeasurement.MaxHeatmapBlocks
            ? $"{report.Blocks.Length} sampled blocks of 64 KiB"
            : $"{report.Blocks.Length} blocks of 64 KiB";

        HasFile = true;
    }
}

/// <summary>One square of the entropy heatmap.</summary>
/// <param name="Entropy">Shannon entropy of the block, 0…8 bits per byte.</param>
/// <param name="ToolTip">Text shown on hover.</param>
public sealed record HeatmapCell(double Entropy, string ToolTip);
