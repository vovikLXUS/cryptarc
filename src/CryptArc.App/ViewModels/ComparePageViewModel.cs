using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CryptArc.App.Localization;

namespace CryptArc.App.ViewModels;

/// <summary>The "Compare codecs" page: every codec on the loaded file, as a chart and a table.</summary>
public partial class ComparePageViewModel : ObservableObject
{
    private static readonly Localizer L = Localizer.Instance;

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
        ? L["Compare.Chart.Speed"]
        : L["Compare.Chart.Size"];

    /// <summary>Creates the page; the chart heading follows the language.</summary>
    public ComparePageViewModel() =>
        L.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ChartTitle));
            for (int i = 0; i < Results.Count; i++)
                Results[i] = Results[i].Retranslated();
        };

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
