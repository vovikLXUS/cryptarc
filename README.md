# CryptArc

Smart encrypting file archiver. Every compression algorithm is implemented from
scratch — no `DeflateStream`, no third-party compression libraries. Cryptography
uses vetted primitives only.

Coursework for *Algorithms and Optimisation of Software Solutions*.

## Status

| Area | State |
|---|---|
| Block codec contract (`ICodec`) | ✅ |
| Store, RLE, Huffman, BWT, LZSS, LZW, Arithmetic, Deflate | ✅ every codec in `CodecId` |
| Bit I/O, CRC-32, entropy analyzer | ✅ |
| Deflate interoperability with RFC 1951 | ✅ checked against a reference implementation both ways |
| Archive container: Argon2id, AES-256-GCM per block, HMAC-SHA256 trailer | ✅ |
| SARC v1 (FORMAT_SPEC_P2): PBKDF2, AES-256-CTR, HMAC, LZ77 + canonical Huffman, volumes by knapsack, `E_*` codes | ✅ all 12 reference archives, cross-read with another team |
| Command line: `analyze`, `test`, `pack`, `unpack`, `list`, `verify`, `sarc-pack`, `sarc-unpack`, `sarc-verify`, `sarc-list` | ✅ |
| Avalonia desktop UI: analyse, compare, compress, transform lab, naive vs optimised, encrypted archive | ✅ |
| Deduplication of repeated chunks | 🚧 planned, not in format version 1 |

## Layout

```
src/CryptArc.Core    algorithms, bit I/O, analyzer, container  (no UI dependencies)
src/CryptArc.Cli     command line front end
src/CryptArc.App     Avalonia desktop application
bench/CryptArc.Bench BenchmarkDotNet suite
tests/CryptArc.Tests xUnit round-trip and unit tests
docs/                format specification and design notes
team/reference       the instructor's SARC v1 reference archives (Project 2)
```

## Build and run

Requires the .NET 10 SDK (pinned in `global.json`).

```bash
dotnet build CryptArc.slnx -c Release
dotnet test  CryptArc.slnx -c Release

dotnet run --project src/CryptArc.Cli -c Release -- analyze <file>
dotnet run --project src/CryptArc.Cli -c Release -- test <file>
dotnet run --project src/CryptArc.Cli -c Release -- pack <archive> <path>...
dotnet run --project src/CryptArc.Cli -c Release -- unpack <archive> [directory]
dotnet run --project src/CryptArc.Cli -c Release -- list <archive>
dotnet run --project src/CryptArc.Cli -c Release -- verify <archive>
dotnet run --project src/CryptArc.Cli -c Release -- sarc-pack <archive.arc> <path>... [--config team_config.json]
dotnet run --project src/CryptArc.Cli -c Release -- sarc-unpack <directory> <volume>...
dotnet run --project src/CryptArc.Cli -c Release -- sarc-verify <volume>...
dotnet run --project src/CryptArc.Cli -c Release -- sarc-list <volume>...
dotnet run --project src/CryptArc.App -c Release

dotnet run --project bench/CryptArc.Bench -c Release -- --filter '*'
```

Every archive command asks for the password, or reads it from one line of
redirected standard input — for scripts:

```bash
echo 'algo2026' | dotnet run --project src/CryptArc.Cli -c Release -- sarc-verify team/reference/ref_01_single_file.arc
```

Exit codes: 0 success, 1 failure (a rejected SARC archive prints its `E_*` code
after `error:`), 2 bad usage.

## Design rules

1. **Compression is written by hand.** Built-in and third-party compressors may
   only appear in benchmarks, as a baseline to compare against.
2. **Cryptography is never written by hand.** AES-256-GCM, PBKDF2/Argon2id and
   SHA-256 come from `System.Security.Cryptography` and Konscious.
3. **Compress, then encrypt.** Ciphertext is incompressible; the other order
   silently destroys the compression ratio.
4. **Keep the naive version.** Every optimised routine keeps its straightforward
   counterpart so the speedup can be measured rather than claimed.

## Documentation

| Document | Answers |
|---|---|
| [ARCHITECTURE.md](ARCHITECTURE.md) | what the system is — layers, abstractions, data structures, complexity |
| [ADR.md](ADR.md) | why it is that way — one record per hard-to-reverse decision |
| [AGENTS.md](AGENTS.md) | how to work in this repo — rules, commands, conventions |
| [docs/container-format.md](docs/container-format.md) | the `.cryptarc` on-disk format |
| [docs/sarc-v1.md](docs/sarc-v1.md) | how SARC v1 (FORMAT_SPEC_P2) is implemented, and its conformance evidence |
| [META-USAGE](META-USAGE) | declaration of generated code, as the course requires |

Every project and every `Core` sub-folder also carries its own `README.md`
(what the folder does) and, where there are constraints to respect, an
`AGENTS.md` (rules for changing it).

`AGENTS.md` is the cross-tool instruction format — Claude Code, OpenAI Codex and
Google Antigravity all read it. The `CLAUDE.md` beside each one is a stub that
imports it, so there is a single source of truth.
