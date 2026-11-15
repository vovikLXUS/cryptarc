# `.cryptarc` container format (v1)

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
├── DATA BLOCKS ─ compressed, then encrypted ───────────────────┤
│ per block:  nonce[12] │ ciphertext[storedSize] │ gcmTag[16]    │
├── TABLE ─ at tableOffset, tableLength bytes on disk ───────────┤
│ nonce[12] │ ciphertext │ gcmTag[16], whose plaintext is:       │
│   codecId u8, originalSize i32, then the compressed bytes of:  │
│ FILE TABLE, entryCount times:                                  │
│   pathLength   u16, path  UTF-8, '/' separated, relative       │
│   originalSize u64                                             │
│   storedSize   u64                                             │
│   firstBlock   u32   index into the block index                │
│   blockCount   u32                                             │
│   modifiedUtc  i64   ticks                                     │
│   attributes   u32                                             │
│   sha256       32 bytes, of the original contents              │
│ BLOCK INDEX: blockCount u32, then per block:                   │
│   offset u64 (of the nonce), storedSize u32, originalSize u32, │
│   codecId u8, crc32 u32 (of the original block)                │
├── TRAILER ─────────────────────────────────────────────────────┤
│ 32  hmacSha256 over everything above, key derived from the     │
│     password with a separate info string                       │
└────────────────────────────────────────────────────────────────┘
```

The table follows the data because its size is known only once every block has
been compressed; `tableOffset` is what makes that possible. Version 1 archives
are always encrypted: `flags` is 1, `kdfId` 2 and `cipherId` 1, and a reader
refuses anything else. The other values are reserved.

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
- **Each GCM operation has associated data**: the block's index as a u32, or
  `0xFFFFFFFF` for the table. A block cannot be moved or passed off as the table.
- **Opening checks the HMAC first**, in constant time, before the table is
  decrypted. KDF parameters are range-checked before that (1-64 passes, 32 KiB to
  2 GiB), since they are still unauthenticated when Argon2 runs.
- **Paths are checked on read and on write**: relative, `/` separated, no empty,
  `.` or `..` segment, no backslash, `:` or NUL. Extraction never overwrites a file.
- **Entries partition the block index** exactly and in order.
- **`formatVersion` is checked on read.** Any change to the field layout bumps it,
  and readers keep support for older versions.

## Codec identifiers

`codecId` in the table prefix and in the block index is one byte. Values are
append-only (ADR-0008); a reader refuses one it does not know.

| Value | Codec | Stream format documented in |
|---:|---|---|
| 0 | Store | `src/CryptArc.Core/Codecs/StoreCodec.cs` — the bytes as they are |
| 1 | RLE | `Codecs/RleCodec.cs` |
| 2 | Huffman | `Codecs/HuffmanCodec.cs` |
| 3 | LZSS | `Codecs/LzssCodec.cs` |
| 4 | Deflate | `Codecs/DeflateCodec.cs` — raw RFC 1951, no zlib or gzip wrapper |
| 5 | LZW | `Codecs/LzwCodec.cs` |
| 6 | BWT | `Codecs/BwtCodec.cs` |
| 7 | Arithmetic | `Codecs/ArithmeticCodec.cs` |

Each codec's stream format is written out in the XML comment of its class; those
comments are part of this specification. The file table itself is always coded
with Deflate (4), or Store (0) when that does not shrink it.

## Key derivation

```
masterKey  = Argon2id(password, salt, t = kdfIterations, m = kdfMemoryKiB, p = 4) → 32 bytes
contentKey = HKDF-SHA256(masterKey, info = "cryptarc:content:v1")
macKey     = HKDF-SHA256(masterKey, info = "cryptarc:mac:v1")
```

The password is normalised to Unicode NFC and encoded as UTF-8 first. The HKDF
step is Expand only, because the Argon2 output is already uniform.

Separate keys for encryption and authentication; never the same bytes for both.
