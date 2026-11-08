# Blocks

- `Compress` and `CompressSequential` must return identical results for the
  same input and options; `BlockCompressorTests.ParallelAgreesWithSequential`
  checks it. Change both or neither.
- Per-block work shares nothing between threads: buffers come from
  `ArrayPool<byte>.Shared` inside the block, results go into a pre-sized array
  slot. No locks, no shared collections.
- A speedup claim needs `BlockCompressorBenchmarks` in Release, with
  `CompressSequential` as the baseline (hard rule 4).
