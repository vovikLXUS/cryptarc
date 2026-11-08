using System.Reflection;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using CryptArc.Bench;

// Run every benchmark:            dotnet run -c Release --project bench/CryptArc.Bench -- --filter *
// Run one class:                  dotnet run -c Release --project bench/CryptArc.Bench -- --filter *Crc32*
// Results land in bench/BenchmarkDotNet.Artifacts (git-ignored); export with --exporters github
// Smoke test, as CI runs it:      dotnet run -c Release --project bench/CryptArc.Bench -- --filter * --job Dry
//
// Every run also writes <Class>.pairs.json next to the other results; copy the ones from
// a full run into bench/results/ to update the GUI's "Naive vs optimised" page.
IConfig config = DefaultConfig.Instance.AddExporter(PairsJsonExporter.Default);
IEnumerable<Summary> summaries = BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args, config);

// BenchmarkDotNet exits 0 even when a benchmark throws. Fail the process so CI notices.
bool failed = summaries.Any(summary =>
    summary.HasCriticalValidationErrors || summary.Reports.Any(report => !report.Success));

return failed ? 1 : 0;
