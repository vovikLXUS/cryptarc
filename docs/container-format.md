# `.cryptarc` container format (draft v1)

All integers are little-endian. Offsets and sizes are in bytes.

## Layout

```
┌── HEADER ─ plaintext, authenticated by the trailer ────────────┐
│  0   8   magic          "CRYPTARC"                             │
│  8   2   formatVersion  u16, currently 1                       │
│ 10   2   flags          u16  bit0 encrypted, bit1 deduplicated │
│ 12   1   kdfId          u8   0 = none, 1 = PBKDF2, 2 = Argon2id│
│ 13   1   cipherId       u8   0 = none, 1 = AES-256-GCM         │
│ 14   2   reserved                                              │
│ 16  16   salt           random, per archive                    │
│ 32   4   kdfIterations  u32  (or Argon2 time cost)             │
│ 36   4   kdfMemoryKiB   u32  (Argon2 only, else 0)             │
│ 40   4   blockSize      u32  uncompressed block size, e.g. 256K│
│ 44   4   entryCount     u32                                    │
│ 48   8   tableOffset    u64                                    │
│ 56   8   tableLength    u64  length on disk, compressed        │
├── FILE TABLE ─ compressed, then encrypted ─────────────────────┤
│ per entry:                                                     │
│   pathLength   u16, path  UTF-8, '/' separated, relative       │
│   originalSize u64                                             │
│   storedSize   u64                                             │
│   firstBlock   u32   index into the block index                │
│   blockCount   u32                                             │
│   modifiedUtc  i64   ticks                                     │
│   attributes   u32                                             │
│   sha256       32 bytes, of the original contents              │
├── BLOCK INDEX ─ compressed, then encrypted ────────────────────┤
│ per block: offset u64, storedSize u32, originalSize u32,       │
│            codecId u8, crc32 u32                               │
├── DATA BLOCKS ─────────────────────────────────────────────────┤
│ per block:  nonce[12] │ ciphertext[storedSize] │ gcmTag[16]    │
├── TRAILER ─────────────────────────────────────────────────────┤
│ 32  hmacSha256 over everything above, key derived from the     │
│     password with a separate info string                       │
└────────────────────────────────────────────────────────────────┘
```

## Rules

- **Order of operations is compress → encrypt.** Encrypting first produces
  high-entropy output that no compressor can shrink.
- **Every block gets its own 96-bit nonce**, drawn from a CSPRNG. A nonce is
  never reused under the same key; reuse breaks GCM catastrophically.
- **The header stays in plaintext** because the reader needs the KDF parameters
  before it has a key. It is covered by the trailer HMAC so an attacker cannot
  silently downgrade `kdfIterations` or `cipherId`.
- **`codecId` is per block, not per archive.** That is what lets the analyzer
  pick a different algorithm for a JPEG and for a log file inside one archive.
- **A block that does not shrink is stored** with `codecId = Store`, bounding
  the worst-case archive size.
- **`formatVersion` is checked on read.** Any change to the field layout bumps it,
  and readers keep support for older versions.

## Key derivation

```
masterKey  = Argon2id(password, salt, t = kdfIterations, m = kdfMemoryKiB, p = 4) → 32 bytes
contentKey = HKDF-SHA256(masterKey, info = "cryptarc:content:v1")
macKey     = HKDF-SHA256(masterKey, info = "cryptarc:mac:v1")
```

Separate keys for encryption and authentication; never the same bytes for both.
