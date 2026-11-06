# Coding

Shared entropy-coding construction. Used by `Codecs/`, depends only on `IO/`.

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
