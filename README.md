# CryptArc

Smart encrypting file archiver. Every compression algorithm is implemented from
scratch — no `DeflateStream`, no third-party compression libraries. Cryptography
uses vetted primitives only.

Coursework for *Algorithms and Optimisation of Software Solutions*.

## Status

| Area | State |
|---|---|
| Block codec contract (`ICodec`) | ✅ |
| Store, RLE, Huffman, BWT | ✅ |
| Bit I/O, CRC-32, entropy analyzer | ✅ |
| LZSS, Deflate, LZW, arithmetic coding | 🚧 planned |
| Archive container + AES-256-GCM | 🚧 planned |
| Avalonia desktop UI | ✅ analyser screen, 🚧 pack/unpack |

## Layout

```
src/CryptArc.Core    algorithms, bit I/O, analyzer, container  (no UI dependencies)
src/CryptArc.Cli     command line front end
src/CryptArc.App     Avalonia desktop application
bench/CryptArc.Bench BenchmarkDotNet suite
tests/CryptArc.Tests xUnit round-trip and unit tests
docs/                format specification and design notes
```

## Build and run

Requires the .NET 10 SDK (pinned in `global.json`).

```bash
dotnet build CryptArc.slnx -c Release
dotnet test  CryptArc.slnx -c Release

dotnet run --project src/CryptArc.Cli -c Release -- analyze <file>
dotnet run --project src/CryptArc.Cli -c Release -- test <file>
dotnet run --project src/CryptArc.App -c Release

dotnet run --project bench/CryptArc.Bench -c Release -- --filter '*'
```

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

Every project and every `Core` sub-folder also carries its own `README.md`
(what the folder does) and, where there are constraints to respect, an
`AGENTS.md` (rules for changing it).

`AGENTS.md` is the cross-tool instruction format — Claude Code, OpenAI Codex and
Google Antigravity all read it. The `CLAUDE.md` beside each one is a stub that
imports it, so there is a single source of truth.
