using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CryptArc.Core;
using CryptArc.Core.Analysis;
using CryptArc.Core.Hashing;

namespace CryptArc.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
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
                await Task.Run(() => Measure(data));

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

    private static (DataProfile Profile, uint Crc, CodecResult[] Results) Measure(byte[] data)
    {
        DataProfile profile = DataAnalyzer.Analyze(data);
        uint crc = Crc32.Compute(data);

        var results = new CodecResult[CodecRegistry.All.Count];

        for (int i = 0; i < results.Length; i++)
        {
            ICodec codec = CodecRegistry.All[i];
            byte[] compressed = new byte[codec.MaxCompressedSize(data.Length)];

            var encodeTimer = Stopwatch.StartNew();
            int compressedLength = codec.Encode(data, compressed);
            encodeTimer.Stop();

            if (compressedLength < 0)
            {
                results[i] = new CodecResult(codec.Name, 0, 1, 0, 0, RoundTripOk: false);
                continue;
            }

            byte[] restored = new byte[data.Length];

            var decodeTimer = Stopwatch.StartNew();
            int restoredLength = codec.Decode(compressed.AsSpan(0, compressedLength), restored);
            decodeTimer.Stop();

            bool ok = restoredLength == data.Length && Crc32.Compute(restored) == crc;

            results[i] = new CodecResult(
                codec.Name,
                compressedLength,
                data.Length == 0 ? 1 : compressedLength / (double)data.Length,
                Throughput(data.Length, encodeTimer.Elapsed),
                Throughput(data.Length, decodeTimer.Elapsed),
                ok);
        }

        return (profile, crc, results);
    }

    private static double Throughput(int bytes, TimeSpan elapsed) =>
        elapsed.TotalSeconds <= 0 ? 0 : bytes / elapsed.TotalSeconds / (1024 * 1024);
}
