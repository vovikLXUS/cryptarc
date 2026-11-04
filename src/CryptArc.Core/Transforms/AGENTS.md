# Transforms

- Every stage here is part of a **persisted stream format**: changing the output
  of `BurrowsWheeler`, `MoveToFront` or `ZeroRunLength` silently breaks every
  archive written with `BwtCodec`. Treat them like `CodecId` — append, never
  redefine.
- Keep `SuffixArray.BuildNaive` next to `SuffixArray.Build`, and keep the test
  that asserts they agree. The pair is benchmark evidence and the naive sort is
  the correctness oracle.
- Inverse stages parse untrusted input: `BurrowsWheeler.Inverse` and
  `ZeroRunLength.Decode` throw `InvalidDataException` on anything inconsistent
  and never loop past the destination length.
- Scratch arrays come from `ArrayPool<T>.Shared` and go back in a `finally`. The
  256-entry tables live on the stack.
