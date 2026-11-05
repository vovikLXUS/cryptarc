# Codecs

One class per compression algorithm, each implementing `ICodec`. This is where
most of the coursework happens.

## Implemented

| File | Algorithm | Complexity | Notes |
|---|---|---|---|
| `StoreCodec.cs` | none (pass-through) | `O(n)` | fallback for incompressible data |
| `RleCodec.cs` | run-length, PackBits style | `O(n)` time, `O(1)` space | worst case grows input by < 0.8 % |
| `LzssCodec.cs` | LZSS, 32 KiB window | `O(n*k)` time with hash chains, `O(n*W)` naive | literal runs bound the worst case at < 0.8 %; `EncodeNaive` and `MatchLengthNaive` are the baselines |
| `LzwCodec.cs` | LZW, 9..12-bit variable width | `O(n)` amortised | own open-addressing hash table |

## Planned

| Algorithm | Owner | Key structure |
|---|---|---|
| Huffman (canonical) | — | min-heap, then lookup-table decoder |
| Deflate (LZSS + Huffman) | — | composition of the two above |
| BWT + MTF + RLE0 + Huffman | — | suffix array |
| Arithmetic / range coder | — | adaptive frequency model |

Fill in the owner column when the work is split.

## Reading order for a new contributor

1. `StoreCodec.cs` — the contract in its simplest possible form.
2. `RleCodec.cs` — the reference for style: the stream format is documented in
   the class comment, `Encode` returns `-1` instead of throwing when the buffer
   is too small, and every group boundary is covered by a test.
3. `../ICodec.cs` — the contract itself.

Write your codec to look like `RleCodec`.
