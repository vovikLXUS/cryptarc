using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CryptArc.App.Services;
using CryptArc.Core;
using CryptArc.Core.Analysis;

namespace CryptArc.App.ViewModels;

/// <summary>State and logic behind the main window: load a file, measure it, show the results.</summary>
/// <param name="measurement">Does the analysis and the per-codec round trips.</param>
public partial class MainWindowViewModel(ICodecMeasurement measurement) : ObservableObject
{
    [ObservableProperty]
    private string _status = "Pick a file to see which codec CryptArc would choose for it.";

    [ObservableProperty]
    private string _fileName = "—";

    [ObservableProperty]
    private string _fileSize = "—";

    [ObservableProperty]
    private string _checksum = "—";

    [ObservableProperty]
    private string _entropy = "—";

    [ObservableProperty]
    private string _repeatRatio = "—";

    [ObservableProperty]
    private string _textRatio = "—";

    [ObservableProperty]
    private string _recommendation = "—";

    [ObservableProperty]
    private bool _isBusy;

    public ObservableCollection<CodecResult> Results { get; } = [];

    /// <summary>
    /// Profiles the file and round-trips it through every implemented codec.
    /// The work runs off the UI thread so the window stays responsive on large files.
    /// </summary>
    public async Task LoadAsync(string path)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        Status = "Reading…";
        Results.Clear();

        try
        {
            byte[] data = await File.ReadAllBytesAsync(path);

            FileName = Path.GetFileName(path);
            FileSize = $"{data.Length:N0} bytes";
            Status = "Analysing…";

            (DataProfile profile, uint crc, CodecResult[] results) =
                await Task.Run(() => measurement.Measure(data));

            Checksum = $"{crc:X8}";
            Entropy = $"{profile.Entropy:F3} bits/byte";
            RepeatRatio = $"{profile.RepeatRatio:P1}";
            TextRatio = $"{profile.TextRatio:P1}";

            CodecId recommended = DataAnalyzer.Recommend(profile);
            Recommendation = CodecRegistry.TryGet(recommended, out _)
                ? recommended.ToString()
                : $"{recommended} (not implemented yet)";

            foreach (CodecResult result in results)
                Results.Add(result);

            Status = $"Done — {results.Length} codec(s) measured.";
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
