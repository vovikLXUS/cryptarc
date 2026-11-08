# Analysis

The "smart" part of the archiver: deciding which algorithm a block should get,
without compressing it several times to find out.

| Type | Role |
|---|---|
| `DataProfile` | immutable statistics of a block |
| `DataAnalyzer` | computes the profile, and maps it to a `CodecId` |
| `ByteHistogram` | counts every byte value over a whole buffer; `Count` uses four interleaved tables, `CountNaive` one |

## How it works

`Analyze` makes **one pass** over at most 64 KiB (`SampleSize`) and produces:

- **Shannon entropy** in bits per byte, 0-8. The theoretical floor for any
  order-0 entropy coder. Near 8 means the data is already compressed.
- **Repeat ratio** — fraction of positions equal to the previous byte. High
  values mean RLE will do well.
- **Text ratio** — fraction of printable ASCII and common whitespace.

`Recommend` turns that into a codec:

| Condition | Codec |
|---|---|
| entropy >= 7.8 | `Store` |
| repeat ratio >= 30 % | `Rle` |
| text ratio >= 90 % and entropy <= 6 | `Bwt` |
| otherwise | `Deflate` |

The result is **advisory**. It may name an algorithm that is not written yet, so
callers go through `CodecRegistry.TryGet` and fall back to `Store`. That is what
allowed selection logic to exist before the codecs did.

See [ADR-0007](../../../ADR.md) for why selection is done by measurement rather
than by trying every codec.

## Byte histogram

`ByteHistogram` counts the whole input, not a sample; the GUI draws it on the
Analyse page. `CountNaive` increments one table per byte, which stalls on runs of
equal bytes: each increment reads the counter the previous one has just written.
`Count` spreads consecutive bytes over four tables and sums them at the end, so
four equal bytes in a row hit four different addresses. `ByteHistogramBenchmarks`
measures the pair on random and run-heavy data.
