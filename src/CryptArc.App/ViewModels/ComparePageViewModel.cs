using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CryptArc.App.ViewModels;

/// <summary>The "Compare codecs" page: every codec on the loaded file, as a chart and a table.</summary>
public partial class ComparePageViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _hasFile;

    /// <summary>False: the chart shows compressed size. True: it shows encode speed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChartTitle))]
    private bool _showSpeed;

    [ObservableProperty]
    private string? _recommendedCodec;

    /// <summary>Heading above the chart; follows <see cref="ShowSpeed"/>.</summary>
    public string ChartTitle => ShowSpeed
        ? "Encode speed, MB/s (higher is better; Store is a plain copy and left out)"
        : "Compressed size, % of original (lower is better)";

    /// <summary>One row per codec, in registry order.</summary>
    public ObservableCollection<CodecResult> Results { get; } = [];

    /// <summary>Replaces the results. Call on the UI thread.</summary>
    /// <param name="results">One row per codec.</param>
    /// <param name="recommendedCodec">Name of the codec the analyzer picks, if it is implemented.</param>
    public void Show(CodecResult[] results, string? recommendedCodec)
    {
        RecommendedCodec = recommendedCodec;

        Results.Clear();
        foreach (CodecResult result in results)
            Results.Add(result);

        HasFile = true;
    }
}
