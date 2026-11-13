using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CryptArc.App.Localization;
using CryptArc.App.Services;

namespace CryptArc.App.ViewModels;

/// <summary>
/// The application shell: which page is open, which file is loaded, the loading
/// itself, and the interface language. The pages only display what the shell hands them.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private static readonly Localizer L = Localizer.Instance;

    private readonly ICodecMeasurement _measurement;
    private readonly ILanguagePreference _languagePreference;

    // What the text properties were built from, so that a language switch can
    // build them again instead of leaving the old language on screen.
    private Func<string> _statusText = () => L["Shell.Status.Initial"];
    private string? _loadedName;
    private long? _loadedLength;
    private MeasurementReport? _report;

    /// <summary>Index of the open page in the side menu: 0 = Analyse, 1 = Compare, 2 = Compress, 3 = Transform lab, 4 = Naive vs optimised.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    private int _selectedPageIndex;

    [ObservableProperty]
    private string _status = L["Shell.Status.Initial"];

    [ObservableProperty]
    private string _fileName = L["Shell.NoFile"];

    [ObservableProperty]
    private string _fileSize = string.Empty;

    [ObservableProperty]
    private bool _hasFile;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>The interface language; setting it switches the whole window and is remembered.</summary>
    [ObservableProperty]
    private AppLanguage _selectedLanguage = L.Language;

    /// <summary>Creates the shell.</summary>
    /// <param name="measurement">Does the analysis and the per-codec round trips.</param>
    /// <param name="languagePreference">Remembers the chosen language between runs.</param>
    /// <param name="analyse">The "Analyse" page.</param>
    /// <param name="compare">The "Compare codecs" page.</param>
    /// <param name="compress">The "Compress" page.</param>
    /// <param name="lab">The "Transform lab" page.</param>
    /// <param name="pairs">The "Naive vs optimised" page.</param>
    public MainWindowViewModel(
        ICodecMeasurement measurement,
        ILanguagePreference languagePreference,
        AnalysePageViewModel analyse,
        ComparePageViewModel compare,
        CompressPageViewModel compress,
        TransformLabPageViewModel lab,
        NaiveVsOptimisedPageViewModel pairs)
    {
        _measurement = measurement;
        _languagePreference = languagePreference;
        Analyse = analyse;
        Compare = compare;
        Compress = compress;
        Lab = lab;
        Pairs = pairs;

        L.LanguageChanged += (_, _) => Retranslate();
    }

    /// <summary>The languages offered by the switch in the side menu.</summary>
    public IReadOnlyList<AppLanguage> Languages => L.Languages;

    /// <summary>The "Analyse" page.</summary>
    public AnalysePageViewModel Analyse { get; }

    /// <summary>The "Compare codecs" page.</summary>
    public ComparePageViewModel Compare { get; }

    /// <summary>The "Compress" page.</summary>
    public CompressPageViewModel Compress { get; }

    /// <summary>The "Transform lab" page.</summary>
    public TransformLabPageViewModel Lab { get; }

    /// <summary>The "Naive vs optimised" page.</summary>
    public NaiveVsOptimisedPageViewModel Pairs { get; }

    /// <summary>The page shown in the content area; a data template picks its view.</summary>
    public object CurrentPage => SelectedPageIndex switch
    {
        1 => Compare,
        2 => Compress,
        3 => Lab,
        4 => Pairs,
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
        SetStatus(() => L["Shell.Status.Reading"]);

        try
        {
            byte[] data = await File.ReadAllBytesAsync(path);

            _loadedName = Path.GetFileName(path);
            _loadedLength = data.LongLength;
            ShowFile();
            SetStatus(() => L["Shell.Status.Analysing"]);

            MeasurementReport report = await Task.Run(() => _measurement.Measure(data));

            _report = report;
            FileSummary summary = FileSummary.From(report);
            Analyse.Show(report, summary);
            Compare.Show(report.Results, summary.RecommendedName);
            Compress.SetFile(data);

            HasFile = true;
            SetStatus(() => L.Format("Shell.Status.Done", report.Crc, report.Results.Length));
        }
        catch (Exception exception)
        {
            // The message comes from the runtime and stays in its language.
            SetStatus(() => L.Format("Common.Failed", exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedLanguageChanged(AppLanguage value)
    {
        // A ComboBox writes null while its items are being replaced; nothing to switch to.
        if (value is null || value == L.Language)
            return;

        L.Use(value);
        _languagePreference.Save(value.Code);
    }

    private void SetStatus(Func<string> text)
    {
        _statusText = text;
        Status = text();
    }

    private void ShowFile()
    {
        FileName = _loadedName ?? L["Shell.NoFile"];
        FileSize = _loadedLength is { } length ? L.Format("Shell.FileSize", length) : string.Empty;
    }

    // The pages that keep their own state retranslate themselves. Analyse only
    // displays the report, so the shell shows it the report again.
    private void Retranslate()
    {
        Status = _statusText();
        ShowFile();

        if (_report is { } report)
            Analyse.Show(report, FileSummary.From(report));
    }
}
