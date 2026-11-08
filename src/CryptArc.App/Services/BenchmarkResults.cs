using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CryptArc.App.Services;

/// <summary>
/// Reads the <c>*.pairs.json</c> files that <c>PairsJsonExporter</c> in
/// <c>bench/CryptArc.Bench</c> writes, as committed in <c>bench/results/</c> and copied
/// next to the executable at build time.
/// </summary>
/// <remarks>
/// The GUI never times anything itself for this page: AGENTS.md accepts a speedup only
/// from a Release BenchmarkDotNet run against the naive version, so the page shows
/// those runs and nothing else.
/// </remarks>
public interface IBenchmarkResults
{
    /// <summary>Folder the result files are read from.</summary>
    string Folder { get; }

    /// <summary>Every suite that has at least one baseline, sorted by name.</summary>
    /// <returns>The suites; empty when the folder is missing or holds no usable file.</returns>
    IReadOnlyList<BenchmarkSuite> Load();
}

/// <summary>Default <see cref="IBenchmarkResults"/>: the <c>benchmarks</c> folder beside the executable.</summary>
public sealed class BenchmarkResults : IBenchmarkResults
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <inheritdoc />
    public string Folder { get; } = Path.Combine(AppContext.BaseDirectory, "benchmarks");

    /// <inheritdoc />
    public IReadOnlyList<BenchmarkSuite> Load()
    {
        var suites = new List<BenchmarkSuite>();
        if (!Directory.Exists(Folder))
            return suites;

        foreach (string path in Directory.EnumerateFiles(Folder, "*.pairs.json"))
        {
            BenchmarkSuite? suite;
            try
            {
                suite = JsonSerializer.Deserialize<BenchmarkSuite>(File.ReadAllText(path), Json);
            }
            catch (JsonException)
            {
                continue; // A malformed file is skipped, not fatal.
            }

            if (suite is { FormatVersion: 1 } && HasBaseline(suite))
                suites.Add(suite);
        }

        suites.Sort((a, b) => string.CompareOrdinal(a.Suite, b.Suite));
        return suites;
    }

    private static bool HasBaseline(BenchmarkSuite suite)
    {
        foreach (BenchmarkGroup group in suite.Groups)
            foreach (BenchmarkMeasurement result in group.Results)
                if (result.Baseline)
                    return true;
        return false;
    }
}

/// <summary>One benchmark class, as written by <c>PairsJsonExporter</c> (format version 1).</summary>
/// <param name="FormatVersion">File format version; only 1 is read.</param>
/// <param name="Suite">Benchmark class name.</param>
/// <param name="MeasuredAt">When the run finished.</param>
/// <param name="Job">BenchmarkDotNet job description; "DefaultJob" for a full run.</param>
/// <param name="Machine">Where it ran.</param>
/// <param name="Groups">One per parameter combination and category set.</param>
public sealed record BenchmarkSuite(
    int FormatVersion,
    string Suite,
    DateTimeOffset MeasuredAt,
    string Job,
    BenchmarkMachine Machine,
    BenchmarkGroup[] Groups)
{
    /// <summary>
    /// True for a short or dry run, whose numbers are too rough for the report.
    /// </summary>
    public bool IsQuickRun => Job.Contains("Short", StringComparison.OrdinalIgnoreCase)
                              || Job.Contains("Dry", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The machine a suite ran on.</summary>
/// <param name="Cpu">Processor name.</param>
/// <param name="Os">Operating system.</param>
/// <param name="Runtime">.NET runtime version.</param>
/// <param name="LogicalCores">Logical processor count.</param>
/// <param name="BenchmarkDotNet">BenchmarkDotNet version.</param>
public sealed record BenchmarkMachine(string Cpu, string Os, string Runtime, int LogicalCores, string BenchmarkDotNet);

/// <summary>One parameter combination and category set: the unit a Ratio is computed over.</summary>
/// <param name="Parameters">Parameter names and values.</param>
/// <param name="Categories">BenchmarkDotNet categories, e.g. Encode or Decode.</param>
/// <param name="Results">Every method measured in this group.</param>
public sealed record BenchmarkGroup(
    Dictionary<string, string> Parameters,
    string[] Categories,
    BenchmarkMeasurement[] Results);

/// <summary>One benchmark method in one group.</summary>
/// <param name="Method">Method name.</param>
/// <param name="Baseline">Whether it is the group's baseline: the naive version.</param>
/// <param name="MeanNs">Mean time per operation in nanoseconds; null if the benchmark failed.</param>
/// <param name="AllocatedBytes">Managed allocation per operation; null if not measured.</param>
public sealed record BenchmarkMeasurement(string Method, bool Baseline, double? MeanNs, long? AllocatedBytes);
