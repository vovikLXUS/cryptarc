# Sarc

SARC v1, the archive format every Project 2 team must read and write, implemented
from the course's `FORMAT_SPEC_P2.md`. It sits next to `Container/` (our own
`.cryptarc` format) and shares only the CRC-32, the match finder, the
length-limited Huffman builder and `LzOutput` with the rest of `Core`.

| File | Spec | Does |
|---|---|---|
| `SarcVolume.cs` | §1, §4.3 steps 1–6 | 41-byte header, HMAC check in constant time, then decryption |
| `SarcCrypto.cs` | §4.1–4.2 | PBKDF2-HMAC-SHA256 (100 000, 64 bytes → `K_enc`, `K_mac`); AES-256-CTR, counter from 1 |
| `SarcPayload.cs` | §2 | file table, block table, safe names, every `E_PAYLOAD` check |
| `SarcBlockCodec.cs` | §3 | LZ77 (3…258, window 32768, greedy or lazy) + three canonical Huffman trees |
| `SarcCodeTable.cs` | §3.3–3.4 | table serialisation and validation (`E_CODE`), canonical codes, symbol decoder |
| `MsbBitStream.cs` | §0 | MSB-first bit writer and reader |
| `SarcVolumePacker.cs` | §7 | volumes by exact 0/1 knapsack (DP, O(n·W)) or greedy by value per byte |
| `SarcArchiveWriter.cs` | — | files → blocks → coding → packing → volumes |
| `SarcArchiveReader.cs` | §4.3 | volumes in any order → files, every failure as `SarcException` with its code |
| `SarcFileClassifier.cs` | §2.2 | rule-based `file_type` until the perceptron exists |
| `SarcErrorCode.cs` | §5 | `E_SHORT` … `E_VOLUMES` |

Naive/optimised pairs (hard rule 4): `AesCtr.TransformNaive` vs `Transform`,
`LinearScanFinder` vs `HashChainFinder` (through the internal `Encode` overload),
`SarcVolumePacker.Greedy` vs `Knapsack`, `SarcParse.Greedy` vs `Lazy`.

The control example of §8 is reproduced byte for byte by
`SarcControlExampleTests`, and `SarcReferenceArchiveTests` runs the whole
reference set in [`team/reference`](../../../team/reference). Design notes and limits are in
[docs/sarc-v1.md](../../../docs/sarc-v1.md).
