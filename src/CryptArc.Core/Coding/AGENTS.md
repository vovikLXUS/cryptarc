# Coding

Shared coding machinery: canonical Huffman construction and Lempel-Ziv match
finding. Used by `Codecs/`; depends on nothing but the framework.

- **Nothing here implements `ICodec`.** These are building blocks. A type that
  compresses a block belongs in `Codecs/`.
- **No hard-coded alphabet.** The alphabet size comes from the span lengths and
  the code-length limit is a parameter. Deflate needs 288, 30 and 19 symbols
  with limits of 15 and 7; anything written for 256 only will have to be undone.
- **Symbol indices do not fit in a byte.** Use `ushort` or `int` for them —
  an alphabet can exceed 256 symbols.
- **Codes come out bit-reversed.** Canonical codes are defined
  most-significant-bit-first, the bit streams are least-significant-first. The
  reversal lives in `AssignCodes` and must not be applied twice.
- **15 bits is the ceiling.** A decode-table entry spends `EntryFieldBits` = 4
  bits on "bits to consume". Raising the limit means widening the entry.
- **Allocate nothing.** `stackalloc` sized from the alphabet; output buffers
  belong to the caller. These routines run once per block.
- Any change here affects every entropy coder at once — `HuffmanCodec`,
  `BwtCodec` through it, and Deflate. Run the full test suite.

## Match finding

- **A finder carries state across calls.** It belongs to one encode pass and is
  never shared between threads. The codec owns the pooled `head`/`prev` arrays.
- **The window must be a power of two** — it masks into `prev`.
- **Positions are asked for in increasing order**, each at most twice in a row,
  because a lazy parser looks one byte ahead. A position is linked into its chain
  only after it has been searched from, so a chain never reaches the present.
- `LinearScanFinder` and `HashChainFinder` must agree byte for byte with an
  unbounded chain and greedy matching. `LzssCodecTests` proves it; keep it true.
