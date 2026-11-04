# CryptArc.Bench

BenchmarkDotNet suite. This is where the numbers in the report come from.

## Suites

| Class | Measures |
|---|---|
| `Crc32Benchmarks` | table-driven CRC vs the per-bit baseline |
| `SuffixArrayBenchmarks` | BWT suffix sorting: prefix doubling vs comparison sort, on random and text |
| `CodecBenchmarks` | encode/decode throughput per codec over random, run-heavy and text data |

## Running

```bash
dotnet run -c Release -- --filter '*Crc32*'
dotnet run -c Release -- --filter '*'                  # everything, takes a while
dotnet run -c Release -- --filter '*' --exporters github
```

`--exporters github` writes a markdown table straight into
`BenchmarkDotNet.Artifacts/results/`, ready to paste into the report.

## Rules

- **Release only.** BenchmarkDotNet refuses Debug, and rightly so.
- Close other applications first; a background build ruins a run.
- Report **allocations alongside time** — `[MemoryDiagnoser]` is on for a reason.
  On .NET, an algorithm that is fast but allocates loses at scale.
- Always quote the machine: CPU, RAM, OS, .NET version. BenchmarkDotNet prints
  all of it in the header; keep that header with the table.
- Compare against a `[Baseline = true]` member so the ratio column does the
  arithmetic for you.

## What belongs here that is banned elsewhere

This is the **only** project allowed to reference a ready-made compressor.
Benchmarking our codecs against `System.IO.Compression.DeflateStream` is a
result worth having; using it in `src/` would defeat the coursework.
