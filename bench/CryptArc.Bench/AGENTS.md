# CryptArc.Bench

- **Release only**, always.
- Every optimisation pair gets a benchmark: mark the slow version
  `[Baseline = true]` so the ratio column is computed for you.
- `[MemoryDiagnoser]` stays on. Time without allocation counts is half a result.
- **Do not** call `Stopwatch` by hand and call it a benchmark. BenchmarkDotNet
  handles warmup, JIT, statistics and outliers; hand timing does none of that.
- Generate input from a **fixed seed** so runs are comparable.
- Build inputs in `[GlobalSetup]`, never inside the measured method.
- Return a value from every benchmark method so the JIT cannot eliminate the
  work as dead code.
- This project may reference `System.IO.Compression` as a baseline. `src/` may
  not — see ADR-0002.
