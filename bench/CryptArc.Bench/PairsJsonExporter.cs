using System.Text.Json;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace CryptArc.Bench;

/// <summary>
/// Writes one compact JSON file per benchmark class, <c>&lt;Class&gt;.pairs.json</c>, that
/// the GUI's "Naive vs optimised" page reads. It keeps what that page needs and the
/// stock JSON exporter lacks in a usable form: which method is the baseline of its
/// group, so the naive/optimised pairs of hard rule 4 can be lined up.
/// </summary>
/// <remarks>
/// <para>Format (version 1):</para>
/// <code>
/// {
///   "formatVersion": 1,
///   "suite": "Crc32Benchmarks",
///   "measuredAt": "2026-11-08T12:00:00Z",
///   "job": "DefaultJob",
///   "machine": { "cpu": "...", "os": "...", "runtime": "...", "logicalCores": 18, "benchmarkDotNet": "0.15.8" },
///   "groups": [
///     { "parameters": { "Size": "4096" }, "categories": [],
///       "results": [ { "method": "Naive", "baseline": true, "meanNs": 1234.5, "allocatedBytes": 0 } ] }
///   ]
/// }
/// </code>
/// <para>
/// A group is one parameter combination plus one category set, the unit
/// BenchmarkDotNet computes its Ratio column over. <c>meanNs</c> is null for a
/// benchmark that failed. <c>job</c> lets a reader refuse numbers from a short or dry
/// run.
/// </para>
/// </remarks>
public sealed class PairsJsonExporter : IExporter
{
    /// <summary>Shared instance.</summary>
    public static readonly PairsJsonExporter Default = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <inheritdoc />
    public string Name => "pairs-json";

    /// <inheritdoc />
    public void ExportToLog(Summary summary, ILogger logger)
    {
        foreach (var suite in Suites(summary))
            logger.WriteLine(JsonSerializer.Serialize(suite.Document, Json));
    }

    /// <inheritdoc />
    public IEnumerable<string> ExportToFiles(Summary summary, ILogger consoleLogger)
    {
        Directory.CreateDirectory(summary.ResultsDirectoryPath);

        foreach (var suite in Suites(summary))
        {
            string path = Path.Combine(summary.ResultsDirectoryPath, $"{suite.Name}.pairs.json");
            File.WriteAllText(path, JsonSerializer.Serialize(suite.Document, Json));
            yield return path;
        }
    }

    private static IEnumerable<(string Name, object Document)> Suites(Summary summary)
    {
        var machine = new
        {
            Cpu = summary.HostEnvironmentInfo.Cpu.Value?.ProcessorName ?? "unknown",
            Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Runtime = summary.HostEnvironmentInfo.RuntimeVersion,
            LogicalCores = Environment.ProcessorCount,
            BenchmarkDotNet = summary.HostEnvironmentInfo.BenchmarkDotNetVersion,
        };

        foreach (var byType in summary.Reports.GroupBy(r => r.BenchmarkCase.Descriptor.Type.Name))
        {
            var groups = byType
                .GroupBy(r => GroupKey(r.BenchmarkCase))
                .Select(group => new
                {
                    Parameters = group.First().BenchmarkCase.Parameters.Items
                        .ToDictionary(p => p.Name, p => p.Value?.ToString() ?? "null"),
                    Categories = group.First().BenchmarkCase.Descriptor.Categories,
                    Results = group.Select(report => new
                    {
                        Method = report.BenchmarkCase.Descriptor.WorkloadMethod.Name,
                        Baseline = report.BenchmarkCase.Descriptor.Baseline,
                        MeanNs = report.ResultStatistics?.Mean,
                        AllocatedBytes = report.GcStats.GetBytesAllocatedPerOperation(report.BenchmarkCase),
                    }).ToArray(),
                })
                .ToArray();

            yield return (byType.Key, new
            {
                FormatVersion = 1,
                Suite = byType.Key,
                MeasuredAt = DateTimeOffset.UtcNow,
                Job = byType.First().BenchmarkCase.Job.DisplayInfo,
                Machine = machine,
                Groups = groups,
            });
        }
    }

    private static string GroupKey(BenchmarkCase benchmark) =>
        benchmark.Parameters.DisplayInfo + "|" + string.Join(",", benchmark.Descriptor.Categories);
}
