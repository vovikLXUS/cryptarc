# Sarc

- **The specification is the course's `FORMAT_SPEC_P2.md`, not ours.** Every
  field, bit order and failure code follows it exactly; "our reader reads our
  archives" proves nothing. When the code and the spec disagree, the code is
  wrong. Never extend the format: the `PREDICTION` flag is rejected with
  `E_PAYLOAD`, as §2.5 requires.
- **Bits are MSB-first here** and only here. Use `MsbBitWriter`/`MsbBitReader`;
  the LSB-first `IO` types must not be touched for SARC.
- **Failures are `SarcException` with the right `SarcErrorCode`.** Pick the code
  from §5 of the spec; the bad reference archives and `expected_failures.json`
  check it. Do not throw anything else from parsing — a test feeds random damage
  to the decoder and to authenticated payloads and expects only `SarcException`.
- **Keep the order of §4.3.** Header checks, then the HMAC in constant time, and
  only then decryption. Decrypting before the HMAC fails criterion C5.
- **Cryptography:** PBKDF2, HMAC and AES come from `System.Security.Cryptography`.
  The CTR construction in `SarcCrypto.cs` (counter blocks encrypted with AES-ECB)
  is the only place a mode is assembled by hand, because .NET has none; see
  ADR-0017. Do not add another.
- **PBKDF2 runs once per archive**, on the writer and the reader side alike.
- **Fresh salt and nonce prefix per archive.** `FixedSalt`/`FixedNoncePrefix`
  are internal and exist only to reproduce the control example.
- **The control example must stay byte-identical.** `SarcControlExampleTests`
  compares the writer's output with `ref_01_single_file.arc`. If it breaks, the
  change broke conformance.
