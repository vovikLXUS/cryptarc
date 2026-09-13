# Hashing

CRC-32 (IEEE 802.3, reflected polynomial `0xEDB88320`) for per-block integrity.

**This is error detection, not security.** CRC catches a truncated download or a
bad sector. It does not stop an attacker: anyone can recompute it after editing
the data. Archive authenticity comes from AES-GCM tags and the trailer
HMAC-SHA256 instead, and those use `System.Security.Cryptography`.

## Two implementations, on purpose

| Method | Approach | Role |
|---|---|---|
| `ComputeNaive` | one shift and conditional XOR per bit | benchmark baseline |
| `Compute` | 256-entry lookup table, one lookup per byte | production |

They must agree bit for bit — a test asserts it across several sizes. The table
version is roughly 8x faster and is the first worked example in the optimisation
section of the report.

Possible next step: slicing-by-8, which processes eight bytes per iteration
using eight tables, for another 2-3x.
