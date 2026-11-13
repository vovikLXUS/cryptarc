using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CryptArc.App.Localization;
using CryptArc.App.Services;
using CryptArc.Core.Analysis;

namespace CryptArc.App.ViewModels;

/// <summary>The "Analyse" page: the file's statistics and the per-block entropy heatmap.</summary>
public partial class AnalysePageViewModel : ObservableObject
{
    private static readonly Localizer L = Localizer.Instance;

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

    /// <summary>Whole-file byte counts, 256 entries; empty until a file is loaded.</summary>
    [ObservableProperty]
    private long[] _histogram = [];

    [ObservableProperty]
    private string _histogramCaption = string.Empty;

    /// <summary>Label of the histogram's vertical axis; the view draws it, the text is decided here.</summary>
    [ObservableProperty]
    private string _histogramAxisTitle = string.Empty;

    /// <summary>One cell per profiled block, in file order.</summary>
    public ObservableCollection<HeatmapCell> Blocks { get; } = [];

    /// <summary>Replaces the page contents with a new report. Call on the UI thread.</summary>
    /// <param name="report">The measurement of the loaded file.</param>
    /// <param name="summary">Whole-file figures aggregated from the blocks.</param>
    public void Show(MeasurementReport report, FileSummary summary)
    {
        Entropy = L.Number(summary.Entropy, "F2");
        RepeatRatio = L.Number(summary.RepeatRatio, "P1");
        TextRatio = L.Number(summary.TextRatio, "P1");
        Recommendation = summary.RecommendedDisplay;
        RecommendationCaption = summary.BlockCount == 0
            ? L["Analyse.Recommended.Empty"]
            : summary.RunnerUp is null
                ? L.Format("Analyse.Recommended.All", summary.BlockCount)
                : L.Format("Analyse.Recommended.Some", summary.RecommendedBlocks, summary.BlockCount, summary.RunnerUp);

        Blocks.Clear();
        foreach (BlockProfile block in report.Blocks)
            Blocks.Add(new HeatmapCell(block.Profile.Entropy,
                L.Format("Analyse.Heatmap.ToolTip", block.Offset, block.Profile.Entropy, block.Profile.TextRatio,
                    DataAnalyzer.Recommend(block.Profile))));

        BlocksCaption = report.Blocks.Length == CodecMeasurement.MaxHeatmapBlocks
            ? L.Format("Analyse.Heatmap.Sampled", report.Blocks.Length)
            : L.Format("Analyse.Heatmap.Blocks", report.Blocks.Length);

        Histogram = report.Histogram;
        HistogramCaption = DescribeHistogram(report.Histogram);
        HistogramAxisTitle = L["Analyse.Histogram.Axis"];

        HasFile = true;
    }

    private static string DescribeHistogram(long[] counts)
    {
        long total = 0;
        int distinct = 0, top = 0;
        for (int value = 0; value < counts.Length; value++)
        {
            total += counts[value];
            if (counts[value] > 0)
                distinct++;
            if (counts[value] > counts[top])
                top = value;
        }

        if (total == 0)
            return L["Analyse.Recommended.Empty"];

        string symbol = top is >= 0x21 and <= 0x7E
            ? $" '{(char)top}'"
            : top == 0x20 ? " " + L["Analyse.Histogram.Space"] : string.Empty;
        return L.Format("Analyse.Histogram.Caption", distinct, top, symbol, counts[top] / (double)total);
    }
}

/// <summary>One square of the entropy heatmap.</summary>
/// <param name="Entropy">Shannon entropy of the block, 0…8 bits per byte.</param>
/// <param name="ToolTip">Text shown on hover.</param>
public sealed record HeatmapCell(double Entropy, string ToolTip);
