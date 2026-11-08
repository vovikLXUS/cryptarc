using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CryptArc.App.Services;

namespace CryptArc.App.ViewModels;

/// <summary>
/// The "Naive vs optimised" page: the optimisation pairs of hard rule 4, as measured by
/// BenchmarkDotNet in Release. The page times nothing itself.
/// </summary>
public partial class NaiveVsOptimisedPageViewModel : ObservableObject
{
    // What each suite compares, in a line. Taken from the benchmark classes' summaries.
    private static readonly Dictionary<string, (string Title, string Story)> Stories = new()
    {
        ["Crc32Benchmarks"] = ("CRC-32", "Per-bit loop → 256-entry lookup table."),
        ["HuffmanBenchmarks"] = ("Huffman decoding", "Bit-by-bit tree walk → two-level lookup table."),
        ["SuffixArrayBenchmarks"] = ("Suffix array (BWT)", "Comparison sort of suffixes → prefix doubling with radix sort. The naive sort is near-quadratic on text."),
        ["MoveToFrontBenchmarks"] = ("Move-to-front", "Byte-by-byte loops → front check, vectorised search and block move."),
        ["LzssMatchSearchBenchmarks"] = ("LZSS match search", "Linear scan of the 32 KiB window → hash chains."),
        ["LzssMatchLengthBenchmarks"] = ("LZSS match length", "One byte per step → 8 bytes per step with TrailingZeroCount."),
        ["DeflateMatchSearchBenchmarks"] = ("Deflate match search", "Linear scan of the 32 KiB window → hash chains, over 3…258 byte matches."),
        ["BackReferenceBenchmarks"] = ("Back-reference copy", "One byte at a time → vectorised copy in blocks of the match distance."),
        ["LzwBenchmarks"] = ("LZW dictionary", "Linear search → open-addressing hash table."),
        ["ArithmeticBenchmarks"] = ("Arithmetic coding", "O(k) cumulative-frequency scans → Fenwick tree with binary lifting."),
        ["BlockCompressorBenchmarks"] = ("Parallel blocks", "One block after another → independent blocks on several threads (ADR-0005)."),
    };

    private readonly IBenchmarkResults _results;
    private IReadOnlyList<BenchmarkSuite> _suites = [];

    [ObservableProperty]
    private SuiteItem? _selectedSuite;

    [ObservableProperty]
    private bool _hasResults;

    [ObservableProperty]
    private string _story = string.Empty;

    [ObservableProperty]
    private string _machine = string.Empty;

    [ObservableProperty]
    private bool _isQuickRun;

    [ObservableProperty]
    private string _bestSpeedup = "—";

    /// <summary>Creates the page and reads the committed results.</summary>
    /// <param name="results">Where the BenchmarkDotNet results come from.</param>
    public NaiveVsOptimisedPageViewModel(IBenchmarkResults results)
    {
        _results = results;
        Reload();
    }

    /// <summary>Folder the results are read from, for the empty-state hint.</summary>
    public string ResultsFolder => _results.Folder;

    /// <summary>One entry per suite for the list on the left.</summary>
    public ObservableCollection<SuiteItem> Suites { get; } = [];

    /// <summary>Every non-baseline method of the selected suite against its group's baseline.</summary>
    public ObservableCollection<PairRow> Rows { get; } = [];

    /// <summary>Reads the result files again.</summary>
    public void Reload()
    {
        _suites = _results.Load();

        Suites.Clear();
        foreach (BenchmarkSuite suite in _suites)
        {
            (string title, _) = Describe(suite.Suite);
            Suites.Add(new SuiteItem(suite.Suite, title, Summarise(Pairs(suite))));
        }

        HasResults = Suites.Count > 0;
        SelectedSuite = Suites.Count > 0 ? Suites[0] : null;
    }

    partial void OnSelectedSuiteChanged(SuiteItem? value)
    {
        Rows.Clear();
        BenchmarkSuite? suite = value is null ? null : _suites.FirstOrDefault(s => s.Suite == value.Name);
        if (suite is null)
        {
            Story = Machine = string.Empty;
            BestSpeedup = "—";
            return;
        }

        Story = Describe(suite.Suite).Story;
        Machine = $"{suite.Machine.Cpu}, {suite.Machine.LogicalCores} logical cores · {suite.Machine.Os} · " +
                  $"{suite.Machine.Runtime} · BenchmarkDotNet {suite.Machine.BenchmarkDotNet} · " +
                  $"measured {suite.MeasuredAt.ToLocalTime():yyyy-MM-dd HH:mm}";
        IsQuickRun = suite.IsQuickRun;

        List<PairRow> pairs = Pairs(suite);
        foreach (PairRow row in pairs)
            Rows.Add(row);

        BestSpeedup = Summarise(pairs);
    }

    // Lines every measured method up against the baseline of its group.
    private static List<PairRow> Pairs(BenchmarkSuite suite)
    {
        var rows = new List<PairRow>();
        foreach (BenchmarkGroup group in suite.Groups)
        {
            BenchmarkMeasurement? baseline = group.Results.FirstOrDefault(r => r.Baseline);
            if (baseline?.MeanNs is not { } baselineNs)
                continue;

            string label = Label(group);
            foreach (BenchmarkMeasurement result in group.Results)
            {
                if (result.Baseline || result.MeanNs is not { } meanNs || meanNs <= 0)
                    continue;

                rows.Add(new PairRow(label, baseline.Method, result.Method, baselineNs, meanNs,
                    baseline.AllocatedBytes, result.AllocatedBytes));
            }
        }

        return rows;
    }

    private static string Summarise(List<PairRow> pairs)
    {
        if (pairs.Count == 0)
            return "—";

        double best = pairs.Max(p => p.Speedup);
        return best >= 1 ? $"up to {best:F1}× faster" : $"{1 / best:F1}× slower";
    }

    private static string Label(BenchmarkGroup group)
    {
        var parts = new List<string>(group.Categories);
        foreach ((string name, string value) in group.Parameters)
            parts.Add($"{name}={value}");
        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }

    private static (string Title, string Story) Describe(string suite) =>
        Stories.TryGetValue(suite, out var story) ? story : (suite.Replace("Benchmarks", string.Empty), string.Empty);
}

/// <summary>An entry of the suite list.</summary>
/// <param name="Name">Benchmark class name.</param>
/// <param name="Title">Human title.</param>
/// <param name="Headline">Best speedup in the suite, as text.</param>
public sealed record SuiteItem(string Name, string Title, string Headline);

/// <summary>One optimised method against the baseline of its group.</summary>
/// <param name="Case">Parameters and categories of the group.</param>
/// <param name="BaselineMethod">The naive method.</param>
/// <param name="Method">The method compared with it.</param>
/// <param name="BaselineNs">Baseline mean, ns per operation.</param>
/// <param name="MeanNs">Method mean, ns per operation.</param>
/// <param name="BaselineAllocated">Baseline allocation per operation, bytes.</param>
/// <param name="Allocated">Method allocation per operation, bytes.</param>
public sealed record PairRow(
    string Case,
    string BaselineMethod,
    string Method,
    double BaselineNs,
    double MeanNs,
    long? BaselineAllocated,
    long? Allocated)
{
    /// <summary>Baseline time over method time; above 1 means the method is faster.</summary>
    public double Speedup => BaselineNs / MeanNs;

    /// <summary>Speedup for display.</summary>
    public string SpeedupText => Speedup >= 1 ? $"{Speedup:F2}× faster" : $"{1 / Speedup:F2}× slower";

    /// <summary>Baseline time for display.</summary>
    public string BaselineText => Time(BaselineNs);

    /// <summary>Method time for display.</summary>
    public string MeanText => Time(MeanNs);

    /// <summary>Allocations, baseline → method, for display.</summary>
    public string AllocatedText => $"{Bytes(BaselineAllocated)} → {Bytes(Allocated)}";

    private static string Time(double ns) => ns switch
    {
        >= 1e9 => $"{ns / 1e9:F2} s",
        >= 1e6 => $"{ns / 1e6:F2} ms",
        >= 1e3 => $"{ns / 1e3:F2} µs",
        _ => $"{ns:F1} ns",
    };

    private static string Bytes(long? bytes) => bytes switch
    {
        null => "?",
        0 => "0 B",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MiB",
        >= 1024 => $"{bytes / 1024.0:F1} KiB",
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
    };
}
