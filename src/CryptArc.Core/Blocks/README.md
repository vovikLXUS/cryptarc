# Blocks

Splitting input into fixed-size independent blocks and compressing them,
optionally in parallel (ADR-0005). It returns per-block results and does not
write a file; `Container/` is what produces archives.

| Type | Role |
|---|---|
| `BlockCompressor` | `Compress` (parallel) and `CompressSequential` (naive baseline) |
| `BlockCompressionOptions` | block size, thread count, fixed codec or automatic choice, verification |
| `BlockResult` | one block: offset, sizes, the codec it ended up with, whether it round-tripped |

## Behaviour

- **Automatic mode** (`Codec = null`) asks `DataAnalyzer.Recommend` for each
  block, so a file of text and random data gets BWT for one part and Store for
  the other.
- **A block never grows.** The destination is exactly the block's size; a codec
  that cannot beat it returns -1 and the block is recorded as `Store`.
- **Verification** decompresses each block and compares it with the original.
  On by default, it roughly doubles the cost.
- **Progress** is reported per finished block, from worker threads and out of
  order when running in parallel.

The GUI's Compress page and the benchmark `BlockCompressorBenchmarks` use this.
