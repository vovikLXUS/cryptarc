# CryptArc.Tests

- **Round-trip is the contract.** `Decode(Encode(x)) == x` must hold for every
  codec over every shape in the theory. If a codec cannot satisfy it, the codec
  is wrong — never the test.
- **Never weaken an assertion to get green.** If a test fails, either the code
  is broken or the expectation was wrong; say which in the commit message.
- Test data is **deterministic** (fixed seeds in `TestData`). A flaky test is
  worse than no test.
- New codecs need no new round-trip test — they are picked up from the registry.
  Add a dedicated file only for behaviour unique to the algorithm.
- **Malformed input must be tested.** Truncated streams, lengths that run past
  the buffer, impossible control bytes. Decoders parse untrusted data, so
  `InvalidDataException` is required behaviour, not a nicety.
- Cover the **boundaries** of every format: the sizes just below, at and above a
  group or window limit, plus empty and single-byte input.
- When an optimised implementation is added, assert that it agrees with the
  naive one rather than re-deriving the expected output by hand.
