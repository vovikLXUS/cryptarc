# Analysis

- **One pass, no allocation.** The histogram is `stackalloc int[256]`. Do not
  introduce `Dictionary`, LINQ or arrays on the heap here — this runs per block.
- **Respect `SampleSize`.** Analysis must stay cheap relative to compression;
  never scan a whole large block.
- **`Recommend` is advisory and may name unimplemented codecs.** Do not "fix"
  that by restricting it to the registry — callers handle the fallback, and this
  is what lets the heuristics be written ahead of the algorithms.
- Thresholds stay **named constants in one place**. When they change, say why in
  the commit message; if the change is structural, add an ADR.
- Any change to the thresholds should be backed by a measurement against
  exhaustive selection (compress with every codec, keep the best), not by
  intuition.
