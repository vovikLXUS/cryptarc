using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CryptArc.App.Services;

namespace CryptArc.App.ViewModels;

/// <summary>
/// The application shell: which page is open, which file is loaded, and the loading
/// itself. The pages only display what the shell hands them.
/// </summary>
/// <param name="measurement">Does the analysis and the per-codec round trips.</param>
/// <param name="analyse">The "Analyse" page.</param>
/// <param name="compare">The "Compare codecs" page.</param>
/// <param name="compress">The "Compress" page.</param>
/// <param name="lab">The "Transform lab" page.</param>
public partial class MainWindowViewModel(
    ICodecMeasurement measurement,
    AnalysePageViewModel analyse,
    ComparePageViewModel compare,
    CompressPageViewModel compress,
    TransformLabPageViewModel lab) : ObservableObject
{
    /// <summary>Index of the open page in the side menu: 0 = Analyse, 1 = Compare, 2 = Compress, 3 = Transform lab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    private int _selectedPageIndex;

    [ObservableProperty]
    private string _status = "Choose a file to analyse it and compare every codec on it.";

    [ObservableProperty]
    private string _fileName = "No file selected";

    [ObservableProperty]
    private string _fileSize = string.Empty;

    [ObservableProperty]
    private bool _hasFile;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>The "Analyse" page.</summary>
    public AnalysePageViewModel Analyse { get; } = analyse;

    /// <summary>The "Compare codecs" page.</summary>
    public ComparePageViewModel Compare { get; } = compare;

    /// <summary>The "Compress" page.</summary>
    public CompressPageViewModel Compress { get; } = compress;

    /// <summary>The "Transform lab" page.</summary>
    public TransformLabPageViewModel Lab { get; } = lab;

    /// <summary>The page shown in the content area; a data template picks its view.</summary>
    public object CurrentPage => SelectedPageIndex switch
    {
        1 => Compare,
        2 => Compress,
        3 => Lab,
        _ => Analyse,
    };

    /// <summary>
    /// Profiles the file and round-trips it through every implemented codec.
    /// The work runs off the UI thread so the window stays responsive on large files.
    /// </summary>
    /// <param name="path">Local path of the file to load.</param>
    public async Task LoadAsync(string path)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        Status = "Reading…";

        try
        {
            byte[] data = await File.ReadAllBytesAsync(path);

            FileName = Path.GetFileName(path);
            FileSize = $"{data.Length:N0} bytes";
            Status = "Analysing…";

            MeasurementReport report = await Task.Run(() => measurement.Measure(data));

            FileSummary summary = FileSummary.From(report);

            Analyse.Show(report, summary);
            Compare.Show(report.Results, summary.RecommendedName);
            Compress.SetFile(data);

            HasFile = true;
            Status = $"CRC-32 {report.Crc:X8} · {report.Results.Length} codecs measured";
        }
        catch (Exception exception)
        {
            Status = $"Failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
