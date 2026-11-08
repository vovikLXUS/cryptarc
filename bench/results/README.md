# bench/results

Committed BenchmarkDotNet results for the optimisation pairs (hard rule 4), one
`<Class>.pairs.json` per benchmark class. The GUI's "Naive vs optimised" page
reads them: the files are copied next to the executable at build time.

They are written by `PairsJsonExporter` (format documented on the class), which
every run of `CryptArc.Bench` adds to the usual exporters.

## Refreshing

Run in Release on a quiet machine, with the default job — not `--job short`;
the page flags short and dry runs as unfit for the report:

```bash
dotnet run --project bench/CryptArc.Bench -c Release -- --filter '*.Crc32Benchmarks.*'   # or several patterns
cp BenchmarkDotNet.Artifacts/results/*.pairs.json bench/results/
```

Leave two files out:

- `CodecBenchmarks.pairs.json` has no baseline; the page would skip it anyway.
- `DeflateVersusReferenceBenchmarks.pairs.json` is not a naive/optimised pair: its
  baseline is .NET's `DeflateStream` encoder, and the group also holds four decode
  benchmarks, so the "speedup" of a decoder over that encoder would be meaningless.
  It belongs in the report as a comparison against the reference, not on this page.

Every file records the CPU, OS, runtime and job it was measured with, and the
page shows them. Numbers from different machines are not comparable; refresh
all files together when the machine changes.
