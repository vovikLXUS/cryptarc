# Transforms

Reversible stages that codecs are assembled from. None of them is a codec on its
own — they do not implement `ICodec` and are not in the registry — but each
reshapes data so that the next stage compresses it better.

## Contents

| File | Stage | Complexity | Notes |
|---|---|---|---|
| `SuffixArray.cs` | suffix sorting | `O(n log n)` | `Build` is prefix doubling with radix sort; `BuildNaive` is the comparison-sort baseline, `O(n^2 log n)` on text |
| `BurrowsWheeler.cs` | Burrows–Wheeler transform | forward: suffix sort + `O(n)`; inverse: `O(n)` | sentinel row returned as the primary index; inverse via LF mapping |
| `MoveToFront.cs` | move-to-front | `O(n*k)` worst, `O(n)` typical | 256-byte list, vectorised search, in place |
| `ZeroRunLength.cs` | RLE0 | `O(n)` | zero runs as bzip2-style RUNA/RUNB digits, in a byte alphabet |

## The BWT pipeline

`Codecs/BwtCodec.cs` chains them in the bzip2 order:

```
block ─► BurrowsWheeler ─► MoveToFront ─► ZeroRunLength ─► HuffmanCodec
          clusters by        clusters to      zero runs to      skewed bytes
          context            small numbers    log2 digits       to short codes
```

Only the last stage shrinks anything by itself. On the repository's own source
and documentation (150 KB) the chain reaches about 26 % of the input size, against
about 62 % for Huffman alone.
