# Hashing

- **CRC-32 is not a security primitive.** Never use it for authentication,
  password checks or tamper detection. Those go through `AesGcm` and
  `HMACSHA256` from `System.Security.Cryptography`.
- Keep **both** `Compute` and `ComputeNaive`, and keep them in agreement — the
  pair is benchmark evidence and the naive version is the correctness oracle.
- The check value for `"123456789"` is `0xCBF43926`. Any change here must still
  produce it.
- If you add slicing-by-8, add it as a third method rather than replacing
  `Compute`, so all three can be compared.
