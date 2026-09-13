# IO

Bit-level streams. Huffman, LZSS and arithmetic coding all emit symbols that are
not byte-aligned, so they build on these two types.

| Type | Purpose |
|---|---|
| `BitWriter` | writes 1-32 bits at a time into a `Span<byte>` |
| `BitReader` | reads them back |

Both are `ref struct`: they borrow a caller-supplied buffer and cannot be boxed,
stored in a field or captured in a lambda. The compiler enforces it.

## Why an accumulator

Bits are staged in a 64-bit register and flushed a whole byte at a time, instead
of one branch per bit. That is roughly an 8-10x difference, and it is the second
worked example in the optimisation section of the report.

## Conventions

- **Least-significant bit first**, everywhere. The encoder and decoder must
  agree; if they disagree the output is silently wrong rather than broken.
- `BitWriter` never writes past the buffer: it sets `Overflowed` and `Flush()`
  returns `-1`.
- `BitReader` zero-extends past the end of input rather than throwing, so a
  decoder can read a trailing partial byte without special-casing it.
