# IO

- Bit order is **LSB-first** throughout. Do not change it in one type only.
- `BitWriter` and `BitReader` are `ref struct` and must stay that way — they
  wrap a borrowed `Span<byte>`.
- **Never allocate here.** No arrays, no strings, no exceptions on the happy
  path — these methods sit in the innermost loop of every entropy coder.
- Overflow is reported, not thrown: `Overflowed` plus `Flush()` returning `-1`.
- Mark the per-bit and per-symbol methods `[MethodImpl(AggressiveInlining)]`.
- Any change here breaks every entropy coder at once. Run the full test suite,
  not just the bit-stream tests.
