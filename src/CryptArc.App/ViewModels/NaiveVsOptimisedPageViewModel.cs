using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CryptArc.App.Localization;
using CryptArc.App.Services;

namespace CryptArc.App.ViewModels;

/// <summary>
/// The "Naive vs optimised" page: the optimisation pairs of hard rule 4, as measured by
/// BenchmarkDotNet in Release. The page times nothing itself.
/// </summary>
public partial class NaiveVsOptimisedPageViewModel : ObservableObject
{
    private static readonly Localizer L = Localizer.Instance;

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
        L.LanguageChanged += (_, _) => Show(SelectedSuite?.Name);
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
        Show(selectName: null);
    }

    // Fills the list and the selected suite's rows from what was read. Titles,
    // stories and speedups are translated text, so this also runs on a language switch.
    private void Show(string? selectName)
    {
        // Cleared first: an entry equal to the old one would not count as a change,
        // and the rows would keep the previous language.
        SelectedSuite = null;

        Suites.Clear();
        foreach (BenchmarkSuite suite in _suites)
        {
            (string title, _) = Describe(suite.Suite);
            Suites.Add(new SuiteItem(suite.Suite, title, Summarise(Pairs(suite))));
        }

        HasResults = Suites.Count > 0;
        SelectedSuite = Suites.FirstOrDefault(s => s.Name == selectName) ?? Suites.FirstOrDefault();
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
        Machine = L.Format("Pairs.Machine", suite.Machine.Cpu, suite.Machine.LogicalCores, suite.Machine.Os,
            suite.Machine.Runtime, suite.Machine.BenchmarkDotNet, suite.MeasuredAt.ToLocalTime());
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
        return best >= 1 ? L.Format("Pairs.Headline.Faster", best) : L.Format("Pairs.Headline.Slower", 1 / best);
    }

    private static string Label(BenchmarkGroup group)
    {
        var parts = new List<string>(group.Categories);
        foreach ((string name, string value) in group.Parameters)
            parts.Add($"{name}={value}");
        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }

    // What each suite compares, in a line: Pairs.Suite.<class>.Title and .Story in the
    // resources, taken from the benchmark classes' summaries. A suite without them
    // still shows, under its class name.
    private static (string Title, string Story) Describe(string suite) =>
        (L.TryGet($"Pairs.Suite.{suite}.Title") ?? suite.Replace("Benchmarks", string.Empty),
         L.TryGet($"Pairs.Suite.{suite}.Story") ?? string.Empty);
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
/// <remarks>
/// The display texts are fixed when the row is created, in the language of that moment.
/// A <c>with</c> copy keeps them, so change a value by creating a new row.
/// </remarks>
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
    public string SpeedupText { get; } = BaselineNs >= MeanNs
        ? Localizer.Instance.Format("Pairs.Row.Faster", BaselineNs / MeanNs)
        : Localizer.Instance.Format("Pairs.Row.Slower", MeanNs / BaselineNs);

    /// <summary>Baseline time for display.</summary>
    public string BaselineText { get; } = Time(BaselineNs);

    /// <summary>Method time for display.</summary>
    public string MeanText { get; } = Time(MeanNs);

    /// <summary>Allocations, baseline → method, for display.</summary>
    public string AllocatedText { get; } = $"{Bytes(BaselineAllocated)} → {Bytes(Allocated)}";

    private static string Time(double ns) => ns switch
    {
        >= 1e9 => Localizer.Instance.Format("Unit.Time.S", ns / 1e9),
        >= 1e6 => Localizer.Instance.Format("Unit.Time.Ms", ns / 1e6),
        >= 1e3 => Localizer.Instance.Format("Unit.Time.Us", ns / 1e3),
        _ => Localizer.Instance.Format("Unit.Time.Ns", ns),
    };

    private static string Bytes(long? bytes) => bytes switch
    {
        null => "?",
        >= 1024 * 1024 => Localizer.Instance.Format("Unit.MiB", bytes / (1024.0 * 1024)),
        >= 1024 => Localizer.Instance.Format("Unit.KiB", bytes / 1024.0),
        _ => Localizer.Instance.Format("Unit.B", bytes),
    };
}
