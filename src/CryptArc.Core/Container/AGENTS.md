# Container

- Cryptography comes from libraries only (hard rule 2), and a block is compressed
  before it is encrypted (hard rule 3).
- Never reuse a nonce: each one is a fresh CSPRNG draw per sealing.
- Every field of the header is unauthenticated until the HMAC is checked. Range-check
  whatever drives work or an allocation before running the KDF.
- A stored path becomes a location on disk. It goes through `IsSafePath` on both
  the write and the read side.
- Any change to the byte layout updates docs/container-format.md and bumps
  `formatVersion` once a released build has written archives.
