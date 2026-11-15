# CryptArc — working notes for Claude Code

Smart encrypting file archiver. University coursework for *Algorithms and
Optimisation of Software Solutions*. The point of the project is the algorithms
themselves, not shipping a product.

Read [ARCHITECTURE.md](ARCHITECTURE.md) before making structural changes, and
[ADR.md](ADR.md) before revisiting a decision it already records.

## How these instructions are organised

`AGENTS.md` is the single source of truth, read by Claude Code, OpenAI Codex and
Google Antigravity alike. Each project and each `Core` sub-folder has its own
`AGENTS.md` holding only the rules specific to that directory — this root file
is not repeated. The `CLAUDE.md` next to each one is a two-line stub that
imports it, so nothing has to be kept in sync.

Tool notes:

- **Codex** concatenates every `AGENTS.md` from the repository root down to the
  directory it is working in; the closest file wins on a conflict. Total budget
  is 32 KiB, which is why the nested files stay short.
- **Antigravity** reads `AGENTS.md` automatically (IDE 1.20.5+) and also accepts
  `.agents/rules/*.md` for workspace rules and `~/.gemini/GEMINI.md` for
  personal ones. Neither is used here — this file covers it.
- **Claude Code** loads `CLAUDE.md`, which pulls this file in via `@AGENTS.md`.

When you add a rule, put it in the `AGENTS.md` closest to the code it governs.

## Hard rules

1. **Compression is written by hand.** Never introduce `System.IO.Compression`
   (`DeflateStream`, `GZipStream`, `ZipArchive`), SharpZipLib, ZstdNet or any
   other compression library into `src/`. They are allowed in `bench/` as a
   baseline to measure against, and in `tests/` as a conformance oracle for a
   published format — Deflate's output has to be readable by a real inflater.
   Never as a step inside a codec: using one in the product code defeats the
   entire purpose of the coursework.
2. **Cryptography is never written by hand.** AES-256-GCM, SHA-256, HMAC and
   PBKDF2 come from `System.Security.Cryptography`; Argon2id from
   `Konscious.Security.Cryptography.Argon2`. Do not implement, "improve" or
   reimplement a cipher, a hash or a KDF. The one exception is the AES-256-CTR
   mode that SARC v1 prescribes and .NET lacks: `Core/Sarc/SarcCrypto.cs`
   assembles it from library AES (ADR-0017), and nowhere else may.
3. **Compress, then encrypt — never the reverse.** Ciphertext is
   indistinguishable from random and will not compress.
4. **Keep the naive implementation.** Every optimised routine keeps its
   straightforward counterpart (a separate method, or behind a flag) so the
   speedup can be measured. These pairs are the core evidence in the report.
5. **`CryptArc.Core` must not reference any UI framework.** No Avalonia, no
   `System.Windows`, no dialogs, no `Console` output from library code.

## Commands

```bash
dotnet build CryptArc.slnx -c Release
dotnet test  CryptArc.slnx -c Release

dotnet run --project src/CryptArc.Cli -c Release -- analyze <file>
dotnet run --project src/CryptArc.Cli -c Release -- test <file>
echo <password> | dotnet run --project src/CryptArc.Cli -c Release -- sarc-verify <volume>...
dotnet run --project src/CryptArc.App -c Release

dotnet run --project bench/CryptArc.Bench -c Release -- --filter '*Crc32*'
```

Always measure in `Release`. Debug numbers are meaningless and must never reach
the report.

## Layout

| Path | Contents | Notes |
|---|---|---|
| `src/CryptArc.Core` | algorithms, bit I/O, analyzer, container, SARC v1 (`Sarc/`) | no UI dependencies |
| `src/CryptArc.Cli` | command line front end | thin, no logic |
| `src/CryptArc.App` | Avalonia desktop app | thin, no logic |
| `bench/CryptArc.Bench` | BenchmarkDotNet suite | the only place a reference compressor may appear |
| `tests/CryptArc.Tests` | xUnit tests | round-trip theories run over the whole registry |
| `docs/` | format spec and design notes | |
| `team/` | the issued Project 2 team package | git-ignored, never committed; `SarcReferenceArchiveTests` runs `team/reference` when it is present |

Each project and each `Core` sub-folder has its own `README.md`; folders with
extra constraints also have an `AGENTS.md`. Read the one nearest to the file you
are editing.

## Adding a codec

1. Implement `ICodec` in `src/CryptArc.Core/Codecs/`.
2. Add the value to `CodecId` — **append only**, never renumber: the value is
   written into archive files.
3. Register the instance in `CodecRegistry`.
4. That is all. The round-trip theory, the CLI `test` command, the GUI table and
   the benchmark parameters all enumerate the registry and pick it up.

`Encode` returns `-1` when the output does not fit. That is a normal signal, not
an error — the container falls back to `Store`. Do not throw for it.

## Conventions

- `Span<T>` / `ReadOnlySpan<T>` in hot paths; no LINQ, no allocations inside
  codec inner loops. `ArrayPool<byte>.Shared` for block buffers.
- Codecs are stateless and thread-safe: blocks are compressed in parallel.
- XML docs on every public member. Document the *stream format* on each codec —
  that is what makes the algorithm reviewable.
- `TreatWarningsAsErrors` is on. Do not silence a warning with a pragma; fix it.
- Target framework, nullability and language version live in
  `Directory.Build.props`. Do not set them per project.

## Tests

- Every codec must satisfy `Decode(Encode(x)) == x` for: empty, single byte,
  constant, alternating, random, mixed and text input. That theory is already
  written and enumerates the registry.
- A malformed or truncated stream must raise `InvalidDataException`, never
  corrupt memory or loop forever. Decoders are parsing untrusted input.
- Do not weaken an assertion to make a test pass.

## Git

- `main` is protected: work through short-lived `feat/*`, `fix/*`, `perf/*`
  branches and squash merge.
- Conventional Commits (`feat:`, `fix:`, `perf:`, `docs:`, `test:`).
- Releases are cut by pushing a `vX.Y.Z` tag; CI builds and publishes.
- Do not commit or push unless asked.
- Code produced with an AI tool is declared in [META-USAGE](META-USAGE) in the
  same change. The course halves the grade for undeclared generated code.

## Agent skills

Skills from [skills.sh](https://skills.sh) are installed in `.agents/skills/`.
Claude Code reaches them through symlinks in `.claude/skills/`. The list,
sources and licenses are in [.agents/skills/README.md](.agents/skills/README.md).
Load the matching skill for these tasks:

| Task | Skill |
|---|---|
| Reviewing a branch or a diff | `code-review` |
| Making a commit | `git-commit` |
| Opening a pull request | `create-pull-request` |
| Writing or running benchmarks | `microbenchmarking` |
| Looking for slow code in a hot path | `analyzing-dotnet-performance` |
| SIMD in a codec | `vectorization` |
| Writing tests | `csharp-xunit` |
| Writing XML docs | `csharp-docs` |

The skills are generic. Where one disagrees with this file, this file wins:

- **code-review:** the standards are this file, the nearest nested `AGENTS.md`
  and `ARCHITECTURE.md`. The spec is the linked issue, or the PR description if
  there is no issue. Ignore the skill's hint to run `/setup-matt-pocock-skills`.
- **git-commit:** use the commit types listed under Git above.
- **create-pull-request:** the template is `.github/PULL_REQUEST_TEMPLATE.md`
  (upper case). The base branch is `main`, and PRs are squash merged, so do not
  rebase or squash locally.
- **microbenchmarking:** permanent benchmarks go in `bench/CryptArc.Bench`. Keep
  the naive implementation as the `Baseline = true` method (hard rule 4).
- **csharp-xunit:** do not add Moq, NSubstitute or FluentAssertions. Codecs are
  pure functions, and plain `Assert` is enough.
- **analyzing-dotnet-performance, vectorization:** a speedup counts only with a
  Release BenchmarkDotNet run against the naive version.
