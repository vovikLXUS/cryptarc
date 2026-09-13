## What changed

<!-- One or two sentences. Link the task if there is one. -->

## Checklist

- [ ] `dotnet test` passes locally
- [ ] New codec (if any) is registered in `CodecRegistry` and covered by the round-trip theory
- [ ] Naive/reference implementation kept behind a flag or a separate method for the benchmark comparison
- [ ] Benchmark numbers attached below if this touches a hot path
- [ ] Public API has XML docs

## Benchmarks

<!-- Paste the BenchmarkDotNet table if this is a perf change. -->
