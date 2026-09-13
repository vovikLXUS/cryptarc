# Codecs

## The contract

```csharp
int Encode(ReadOnlySpan<byte> source, Span<byte> destination);  // bytes written, or -1
int Decode(ReadOnlySpan<byte> source, Span<byte> destination);  // bytes written
int MaxCompressedSize(int sourceLength);                        // true upper bound
```

- `Encode` returns **-1** when the output does not fit. This is a normal signal,
  not an error: the container falls back to `Store`. Never throw for it.
- `Decode` throws **`InvalidDataException`** for truncated or malformed input.
  Decoders parse untrusted bytes: bounds-check every length read from the
  stream before using it. Never read or write past a span.
- `MaxCompressedSize` must be a genuine upper bound including the incompressible
  worst case. Callers size their buffers from it; underestimating corrupts data.
- Codecs are **stateless**. No mutable fields — blocks compress in parallel.

## Required of every new codec

1. **Document the stream format** in the class XML comment: what each control
   byte, flag bit and field means. A format that is not written down cannot be
   reviewed or debugged.
2. **State the complexity** in the comment, time and space.
3. **Append** to `CodecId` (never renumber — the value is stored in archives)
   and register in `CodecRegistry`. The round-trip theory, CLI `test`, the GUI
   table and the benchmarks then pick it up automatically.
4. **Keep the naive version** next to the optimised one — a separate method or
   an `#if` — so the speedup can be measured for the report.
5. **Bound the worst case.** An algorithm that can double incompressible input
   is a bug; emit literal runs like `RleCodec` does.

## Testing

`tests/CryptArc.Tests/Codecs/CodecRoundTripTests.cs` already runs every
registered codec over empty, single-byte, constant, alternating, random, mixed
and text input. Add a dedicated test file only for behaviour specific to your
algorithm — boundary group sizes, malformed-stream rejection, ratio claims.
