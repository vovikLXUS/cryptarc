# CryptArc.Core

The whole archiver lives here. Everything else in the solution is a front end
over this library.

**This project must not reference any UI framework.** No Avalonia, no
`Console.WriteLine` from library code, no file dialogs. That rule is what makes
the algorithms testable and benchmarkable on their own.

## Contents

| Folder | Purpose |
|---|---|
| `Codecs/` | compression algorithms, one class per algorithm |
| `Transforms/` | reversible stages codecs are built from: suffix array, BWT, move-to-front, zero runs |
| `IO/` | bit-level reading and writing |
| `Hashing/` | CRC-32 for block integrity |
| `Analysis/` | entropy statistics and codec selection |
| *(root)* | `ICodec`, `CodecId`, `CodecRegistry` — the contracts everything hangs off |

Planned: `Container/` — archive header, file table, block index, encryption.

## The three root types

- **`ICodec`** — the interface every algorithm implements. Two methods and an
  upper-bound estimate.
- **`CodecId`** — a byte written into archive files, so it is append-only.
- **`CodecRegistry`** — maps ids to the implementations that exist. Tests, CLI,
  GUI and benchmarks all enumerate it, so registering a codec is enough to make
  it appear everywhere.

See [ARCHITECTURE.md](../../ARCHITECTURE.md) for the full picture.
