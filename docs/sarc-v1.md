# SARC v1 — implementation notes

The format itself is defined by the course's `FORMAT_SPEC_P2.md` (Project 2) and
is not repeated here. This page records the choices the specification leaves to
each team, the order in which the reader checks things, and the known limits.
Code: [`src/CryptArc.Core/Sarc`](../src/CryptArc.Core/Sarc).

## Conformance evidence

| Check | Result |
|---|---|
| §8 control example: canonical codes, the 57-byte block, the 172-byte volume | reproduced byte for byte (`SarcControlExampleTests`) |
| `ref_01` … `ref_06`, the 3-volume one given in reverse order | all files restored, sizes and CRC-32 as in the `.json` manifests |
| `bad_01` … `bad_06` | rejected with exactly the code in `expected_failures.json` |
| Cross-team: our volumes read by team T01's archiver, theirs read by ours | 31 files, 20 volumes each way, byte-identical |

The reference archives belong to the issued team package (`team/reference/`) and
are not committed here; the control example is embedded in the tests because the
specification defines it completely.

## Choices the specification leaves open

| Field / question | This implementation |
|---|---|
| `strategy` | 0 — stored raw (already-compressed data); 1 — LZ77 lazy + Huffman; 2 — LZ77 greedy + Huffman |
| `file_type` | `SarcFileClassifier`: file signatures, entropy and punctuation. A stand-in for the perceptron of zone "Аналіз файлу" |
| Empty file | one block, `raw_len = coded_len = 0`, `NO_COMPRESSION` — as `ref_02` does |
| When a block is stored raw | strategy 0, or the coded block is not smaller than the raw one |
| Nonce | 8 random bytes per archive ‖ `uint32_BE(volume_index)` |
| Password encoding | UTF-8 exactly as typed, no Unicode normalisation (another team's reader would not normalise) |
| Volume file names | one volume: the name given; several: `name.part0.arc`, `name.part1.arc`, … |
| Knapsack value | `пріоритети_типів` from `team_config.json`; a priority of 0 counts as 1 so that every volume takes a block |
| Knapsack size | exact DP up to 2²⁶ cells (n·W); a larger volume falls back to greedy |

## Reader order and codes

1. Per volume: length ≥ 73 → `E_SHORT`; magic and version → `E_MAGIC`; length against
   `ciphertext_len` → `E_LENGTH`.
2. All volumes carry one salt, else `E_VOLUMES` (they belong to different archives).
3. PBKDF2 once; per volume the HMAC in constant time → `E_HMAC`; only then decryption.
4. Payload: `reserved`, `flags` (`PREDICTION` is refused), `file_type`, safe names,
   table and data lengths → `E_PAYLOAD`.
5. Blocks: tables (alphabet, order, lengths 1…15, Kraft equality, a lone symbol of
   length 1), codes, references, padding → `E_CODE`; lengths that disagree, bytes
   after the stream → `E_PAYLOAD`.
6. The set: count, duplicates, index range, one `volume_count` → `E_VOLUMES`.
7. Each file joined by (name, `block_seq`): gap-free from 0, summing to `orig_size`,
   sizes and CRC agreeing between volumes → `E_PAYLOAD`.
8. CRC-32 of each file → `E_CRC`.

Nothing is written until every check has passed, and extraction never replaces an
existing file.

## Command line

```bash
dotnet run --project src/CryptArc.Cli -c Release -- sarc-pack backup.arc docs README.md --config team/team_config.json
dotnet run --project src/CryptArc.Cli -c Release -- sarc-unpack restored backup.part1.arc backup.part0.arc
dotnet run --project src/CryptArc.Cli -c Release -- sarc-verify backup.part*.arc
```

A rejection prints `error: E_<CODE>: <detail>` on standard error and exits with 1.

## Limits

- Files and volumes are held in memory; files larger than RAM are not supported yet
  (criterion C2, item 4).
- `orig_size` is 32 bits, `volume_count` 16 bits; the writer refuses archives beyond them.
- Names are bare file names, as version 1 requires. Packing a directory flattens it,
  and two files with the same name are refused.
