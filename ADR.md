# Architecture Decision Records

Each entry records one decision that was hard to reverse, the situation that
forced it, and what it costs us. The point is that six months from now — or at
the defence — nobody has to guess *why* the code looks like this.

Format: **Context** (the problem) → **Decision** (what we chose) →
**Consequences** (what it buys and what it costs) → **Alternatives** (what we
rejected and why).

Rules for this file:

- One record per decision, numbered, append-only.
- Never edit a record after it is accepted. To change your mind, add a new
  record and set the old one to `Superseded by ADR-00NN`.
- Only decisions that are expensive to undo. "Renamed a variable" is not an ADR.

For *what* the system looks like, see [ARCHITECTURE.md](ARCHITECTURE.md).

## Index

| # | Decision | Status |
|---|---|---|
| [0001](#adr-0001--c-and-net-10-with-avalonia) | C# and .NET 10 with Avalonia | Accepted |
| [0002](#adr-0002--compression-written-by-hand-cryptography-never) | Compression written by hand, cryptography never | Accepted |
| [0003](#adr-0003--one-icodec-interface-for-every-algorithm) | One `ICodec` interface for every algorithm | Accepted |
| [0004](#adr-0004--encode-returns--1-instead-of-throwing) | `Encode` returns `-1` instead of throwing | Accepted |
| [0005](#adr-0005--fixed-size-independent-blocks) | Fixed-size independent blocks | Accepted |
| [0006](#adr-0006--compress-then-encrypt) | Compress, then encrypt | Accepted |
| [0007](#adr-0007--per-block-codec-selection-from-a-cheap-statistical-profile) | Per-block codec selection from a cheap statistical profile | Accepted |
| [0008](#adr-0008--codecid-is-persisted-and-append-only) | `CodecId` is persisted and append-only | Accepted |
| [0009](#adr-0009--keep-the-naive-implementation-next-to-the-optimised-one) | Keep the naive implementation next to the optimised one | Accepted |
| [0010](#adr-0010--the-archive-header-stays-in-plaintext) | The archive header stays in plaintext | Accepted |
| [0011](#adr-0011--core-has-no-ui-dependency) | `Core` has no UI dependency | Accepted |
| [0012](#adr-0012--trunk-based-git-with-releases-cut-from-tags) | Trunk-based git with releases cut from tags | Accepted |
| [0013](#adr-0013--deflate-follows-rfc-1951-to-the-bit) | Deflate follows RFC 1951 to the bit | Accepted |
| [0014](#adr-0014--plain-microsoftextensionsdependencyinjection-in-the-gui-not-prism) | Plain `Microsoft.Extensions.DependencyInjection` in the GUI, not Prism | Accepted |

---

## ADR-0001 — C# and .NET 10 with Avalonia

**Status:** Accepted · 2026-09-12

### Context

The coursework is graded on algorithms and on measurable optimisation, so we
need a language where profiling, threading and memory layout are visible and
controllable. We also need a desktop GUI, and the team has to be productive in
it within one semester. C++ gives the most control but costs the most time;
Python makes the optimisation section meaningless because the interesting work
would be "we rewrote it in C".

### Decision

C# on .NET 10 (LTS). Avalonia for the desktop UI. The target framework is pinned
in `Directory.Build.props` and the SDK in `global.json`.

### Consequences

- `Span<T>`, `stackalloc`, `ArrayPool<T>`, `System.Numerics` SIMD and `unsafe`
  give us a real optimisation story without leaving the language.
- BenchmarkDotNet produces publication-quality tables straight into the report.
- Avalonia builds on Windows, Linux and macOS from the same source, so CI can
  produce all three in one release workflow.
- The GC is a confounder in measurements. Mitigated by `ServerGarbageCollection`
  and by BenchmarkDotNet's `MemoryDiagnoser`, but "allocations per operation"
  has to be reported alongside time.
- .NET 10 is not installed by default on Windows; releases are published
  self-contained and single-file.

### Alternatives

- **C++20** — best control, highest cost. Rejected on schedule risk.
- **Rust** — attractive, but nobody on the team knows it well enough.
- **Python** — prototyping only; nothing meaningful to optimise.
- **WPF/WinForms** — Windows-only, and cross-platform CI was worth keeping.

---

## ADR-0002 — Compression written by hand, cryptography never

**Status:** Accepted · 2026-09-12

### Context

Two opposite temptations. For compression, calling `DeflateStream` would produce
a working archiver in an afternoon — and would remove the entire subject of the
coursework. For cryptography, writing our own AES looks impressive and is in
practice a guaranteed vulnerability: side channels, nonce misuse and key
handling are where real implementations fail, and none of that is examinable
here anyway.

### Decision

- Every compression algorithm is implemented from scratch in `src/`. No
  `System.IO.Compression`, no SharpZipLib, no ZstdNet.
- Reference compressors may appear in `bench/` only, as a baseline to compare
  against.
- All cryptographic primitives come from `System.Security.Cryptography`
  (AES-256-GCM, SHA-256, HMAC, PBKDF2) and `Konscious.Security.Cryptography`
  (Argon2id). We write no cipher, hash or KDF.

### Consequences

- The codec work is the deliverable, and the benchmark against `DeflateStream`
  becomes a headline result rather than an embarrassment.
- Our archives are not zip-compatible. Accepted: interoperability was never a
  goal.
- Bugs in our codecs corrupt data, so the round-trip property test is
  non-negotiable and runs over every registered codec.
- The security section of the report argues from primitive choice and protocol
  construction, not from implementation.

### Alternatives

- **Use a library and "focus on the UI"** — rejected; it deletes the coursework.
- **Write our own AES for extra credit** — rejected; it is an anti-pattern, and
  presenting home-made crypto as secure would be worse than not having it.

---

## ADR-0003 — One `ICodec` interface for every algorithm

**Status:** Accepted · 2026-09-12

### Context

Six or seven algorithms with very different internals (byte-oriented RLE,
bit-oriented Huffman, window-based LZSS, block-sorting BWT) all have to be
selectable at runtime, compared in benchmarks, and tested identically. Without a
common shape, every consumer grows a `switch` over algorithms and every new
codec means editing the CLI, the GUI, the tests and the container.

### Decision

A single interface — `Encode`, `Decode`, `MaxCompressedSize`, plus an id and a
name — over spans. Implementations are stateless. `CodecRegistry` maps ids to
instances, and every consumer enumerates the registry instead of naming codecs.

### Consequences

- Adding an algorithm touches exactly three files; tests, CLI output, GUI table
  and benchmark parameters pick it up with no further edits.
- Statelessness means blocks can be compressed in parallel with no locking.
- Span-based signatures rule out streaming codecs that need to hold state across
  calls. Acceptable because the container is block-based anyway (see ADR-0005).
- The interface is the narrowest point of the system, so it is where the error
  contract has to be exact — hence ADR-0004.

### Alternatives

- **`Stream`-based API** — idiomatic .NET, but forces allocation and hides the
  buffer boundaries that the block model depends on.
- **A base class instead of an interface** — would invite shared mutable state,
  which breaks parallel compression.

---

## ADR-0004 — `Encode` returns `-1` instead of throwing

**Status:** Accepted · 2026-09-12

### Context

Incompressible data makes every real codec produce more output than input. This
is expected, happens constantly (JPEG, MP4, already-zipped files), and must be
cheap to handle. Using an exception for it would put a throw on a hot, ordinary
path and would blur the line with genuinely malformed input.

### Decision

- `Encode` returns `-1` when the output does not fit. The caller falls back to
  `Store`. This is a normal control-flow signal, not an error.
- `Decode` throws `InvalidDataException` for truncated or malformed streams,
  because that *is* an error and the data is untrusted.

### Consequences

- The "compress or store, whichever is smaller" rule is a comparison, not a
  `try`/`catch`, and the archive can never be meaningfully larger than the input.
- Every codec must bounds-check writes and return `-1` rather than overrun the
  destination span. Tested explicitly.
- Two different failure modes in one interface — documented on `ICodec` and in
  `Codecs/CLAUDE.md` so it is not learned by surprise.

### Alternatives

- **Throw on both** — exceptions on a routine path, and a real risk of catching
  a corruption bug and silently storing the block instead.
- **`TryEncode(out int)`** — equivalent, but noisier at every call site.

---

## ADR-0005 — Fixed-size independent blocks

**Status:** Accepted · 2026-09-12

### Context

A whole-file compression stream is simple to write and bad at everything else:
it cannot be parallelised, it needs the whole file in memory, extracting one
small file means decompressing everything before it, and one corrupt byte
destroys the remainder.

### Decision

Split input into fixed-size blocks (256 KiB by default). Each block is analysed,
compressed, encrypted and checksummed independently. A block index records each
block's offset, sizes, codec and CRC.

### Consequences

- Compression parallelises across cores with `Parallel.For`. The speedup-versus-
  threads curve is the central measurement of the optimisation section, and
  Amdahl's law has something real to be applied to.
- Memory is bounded regardless of input size.
- Selective extraction and integrity checking per block become possible.
- **Compression ratio drops slightly**: each block restarts its dictionary and
  its Huffman tables, so cross-block redundancy is lost. At 256 KiB the loss is
  small; the block size is a tunable knob and its effect on ratio is itself
  worth a benchmark.
- Per-block overhead (nonce, tag, index entry ≈ 45 bytes) matters for archives
  of many tiny files. Deduplication and block packing are the planned answer.

### Alternatives

- **One stream per file** — better ratio on large files, no parallelism, no
  random access.
- **Content-defined variable blocks** — better for deduplication, more complex;
  kept as a possible follow-up on top of the fixed-block index.

---

## ADR-0006 — Compress, then encrypt

**Status:** Accepted · 2026-09-12

### Context

The order of the two transforms is not a style question. Good ciphertext is
statistically indistinguishable from random, so its entropy is ~8 bits per byte
and no compressor can shrink it. Encrypting first would reduce every archive to
`Store` and quietly delete the compression feature.

### Decision

Every block is compressed first and the compressed bytes are then encrypted with
AES-256-GCM under a per-block nonce.

### Consequences

- Compression works as intended.
- The *compressed size* of a block leaks information about its content even
  though the content is encrypted. This is the known CRIME/BREACH shape of
  attack. It matters when an attacker can inject chosen data into a stream they
  also observe; for an offline file archiver the exposure is much weaker. We
  accept it and state it explicitly rather than pretend it does not exist.
- Padding compressed blocks to a fixed size would close the leak at a real cost
  in ratio. Not done; recorded here as the mitigation if the threat model ever
  changes.

### Alternatives

- **Encrypt then compress** — destroys compression. Non-starter.
- **Compress, pad, encrypt** — hides sizes, costs ratio. Rejected for now.

---

## ADR-0007 — Per-block codec selection from a cheap statistical profile

**Status:** Accepted · 2026-09-12

### Context

"Smart" has to mean something measurable. A single algorithm for every input is
wrong in both directions: running Huffman over an MP4 wastes CPU for nothing,
while storing a log file wastes most of the available ratio. The obvious
alternative — try every codec and keep the best — costs *k* full compressions
per block.

### Decision

`DataAnalyzer` takes one pass over at most 64 KiB of each block and computes
Shannon entropy, adjacent-byte repeat ratio and printable-text ratio.
`Recommend` maps that profile onto a `CodecId` through explicit thresholds. The
recommendation is advisory: the caller resolves it through
`CodecRegistry.TryGet` and falls back to `Store`.

### Consequences

- Selection costs one cheap pass instead of k compressions.
- Different blocks of the same archive can use different algorithms — a JPEG is
  stored while the README next to it is block-sorted. This is the feature the
  GUI shows off.
- The analyzer may name a codec nobody has written yet, which is what let the
  selection logic land before Huffman, LZSS or BWT existed.
- Heuristics will sometimes be wrong. Thresholds are named constants in one
  place and their accuracy can be measured against exhaustive selection.
- A sampled prefix can misrepresent a block whose character changes halfway
  through.

### Alternatives

- **Try all codecs, keep the smallest** — optimal ratio, k times the cost. Worth
  keeping as a `--best` mode and as the ground truth to score the heuristic.
- **Extension-based rules only** — free, but wrong for `.dat`, `.bin`, `.tmp`
  and anything renamed.
- **A trained decision tree** — the planned upgrade; the profile already carries
  the features it would need.

---

## ADR-0008 — `CodecId` is persisted and append-only

**Status:** Accepted · 2026-09-12

### Context

The codec id is written into every block header. If the enum is ever renumbered,
previously written archives silently decode with the wrong algorithm — not an
error, just corruption.

### Decision

`CodecId` is a `byte` enum with explicit values. Members may only be appended;
existing values are never changed or reused. The container additionally carries
a `formatVersion`, and readers refuse versions they do not understand.

### Consequences

- Archives written by an early build stay readable.
- Removing an algorithm still requires keeping its id reserved and its decoder
  available, or bumping `formatVersion`.
- 256 possible codecs, which is far more than this project will ever need.

### Alternatives

- **String names in the header** — self-describing, larger, and invites typos.
- **No versioning** — fine until the first format change, then silent breakage.

---

## ADR-0009 — Keep the naive implementation next to the optimised one

**Status:** Accepted · 2026-09-12

### Context

The course is graded on optimisation, and an optimisation that cannot be
measured cannot be defended. "We used hash chains and it got much faster" is an
assertion; a table with two rows from the same machine is evidence. Once the
slow version is deleted, the comparison is gone for good.

### Decision

Every optimised routine keeps its straightforward counterpart — a separate
method (`Crc32.ComputeNaive`), a flag, or an `#if` — and both are covered by
tests asserting they produce identical output. Benchmarks compare them directly.

### Consequences

- The report's before/after tables come straight out of BenchmarkDotNet.
- The naive version doubles as a correctness oracle: when the fast path is
  wrong, the slow one says so.
- More code to maintain, and the naive paths must stay correct even though
  nothing ships through them. Accepted — they are small by definition.

### Alternatives

- **Measure once, then delete** — numbers rot, and they cannot be re-run on the
  examiner's question.
- **Git history as the archive** — technically there, practically unusable for a
  side-by-side benchmark.

---

## ADR-0010 — The archive header stays in plaintext

**Status:** Accepted · 2026-09-12

### Context

To derive the key, the reader needs the salt, the KDF identifier, the iteration
and memory costs and the cipher id. All of those live in the header, so the
header cannot itself be encrypted — there is nothing to decrypt it with yet. But
an unprotected header can be edited by an attacker to downgrade `kdfIterations`
to 1 or `cipherId` to "none".

### Decision

The header is stored in plaintext and authenticated: a trailer holds
HMAC-SHA256 over the entire archive, computed with a MAC key derived separately
from the master key. The file table and block index are compressed and encrypted
like the data.

### Consequences

- Tampering with KDF or cipher parameters is detected before any data is trusted.
- File names, sizes and timestamps stay confidential because the table is
  encrypted; only the archive-level parameters and the total size are visible.
- Verification requires reading the whole archive, so `verify` is a full pass.
- Confidentiality and authentication use separate derived keys, never the same
  bytes.

### Alternatives

- **Encrypt the header too** — impossible without a chicken-and-egg key.
- **Leave it unauthenticated** — enables silent downgrade attacks.

---

## ADR-0011 — `Core` has no UI dependency

**Status:** Accepted · 2026-09-12

### Context

The archiver has two front ends, a CLI and an Avalonia desktop app. If progress
reporting, error dialogs or console output leak into the library, the algorithms
can only run inside an application, and benchmarking or testing them means
dragging a UI framework along.

### Decision

`CryptArc.Core` references no UI framework and writes nothing to the console. It
reports progress through `IProgress<T>` and failures through exceptions or
return values. Both front ends are thin adapters containing no logic.

### Consequences

- Algorithms are testable and benchmarkable in isolation; `CryptArc.Tests` and
  `CryptArc.Bench` reference `Core` alone.
- The same library serves the CLI and the GUI with no duplication.
- Long-running work must be pushed off the UI thread by the caller — the GUI
  wraps it in `Task.Run` and marshals updates back through the dispatcher.
- A little ceremony: no `Console.WriteLine` while debugging library code.

### Alternatives

- **One project for everything** — simpler at first, untestable and unbenchmark-
  able later.
- **Core depends on Avalonia for progress types** — couples the algorithms to a
  UI framework for no benefit.

---

## ADR-0012 — Trunk-based git with releases cut from tags

**Status:** Accepted · 2026-09-12

### Context

The usual suggestion is a `main` plus `staging` pair. That model comes from web
deployment, where a branch maps to a server environment. A desktop application
has no environments and nothing to deploy to, so a second long-lived branch
would only add merge work. There is also no "review app" URL to open on a pull
request.

### Decision

- One long-lived branch, `main`, protected: pull request, green CI, one approval,
  squash merge.
- Short-lived `feat/*`, `fix/*`, `perf/*` branches.
- Releases are cut by pushing a `vX.Y.Z` tag; CI builds win-x64, linux-x64 and
  osx-arm64 and publishes a GitHub Release. Pre-release channels are expressed
  as tag suffixes (`-beta.1`), not as branches.
- The reviewable artifact on a pull request is the built binary, uploaded by CI
  and downloaded from the Actions tab.

### Consequences

- No recurring `staging → main` merges and no drift between branches.
- Release state is a tag, so any version can be rebuilt exactly.
- Reviewers must download an artifact to try a change — slower than clicking a
  preview link, but it is the real application.
- If the course requires two branches for form's sake, a `dev` integration
  branch can be added without changing any of the above.

### Alternatives

- **GitFlow with `develop` and `release/*`** — designed for versioned parallel
  releases; overhead we have no use for.
- **`staging` branch** — solves a deployment problem this project does not have.

---

## ADR-0013 — Deflate follows RFC 1951 to the bit

**Status:** Accepted · 2026-11-06

### Context

Deflate is the last codec and the only one in the project that implements a
format somebody else published. That leaves a choice the other codecs never had:
produce a real RFC 1951 stream, or a simpler "Deflate-like" format of our own
with the same two ideas (LZ77 plus Huffman) but our own headers.

The simpler variant is genuinely cheaper. The real format demands the HCLEN
permutation, the 16/17/18 code-length repeaters, and the exact extra-bit tables
for 29 length codes and 30 distance codes — a lot of table data that is easy to
get subtly wrong and whose errors produce streams that look plausible and decode
to garbage.

Against that: every other codec here can only be checked against itself. A
round-trip test proves the encoder and decoder agree, not that either is right.
Two mistakes that cancel out stay invisible.

### Decision

Implement RFC 1951 exactly, with no zlib or gzip wrapper. `DeflateStream` then
becomes a correctness oracle: our output must inflate under a real
implementation, and our decoder must read what a real implementation deflates.

Hard rule 1 is relaxed for `tests/` only: a reference compressor may appear
there as an oracle, never as a step inside a codec. `bench/` already allowed it
as a performance baseline.

### Consequences

- Deflate gets a class of verification nothing else in the project has, and it
  catches exactly the mistakes a round-trip test cannot see.
- `.cryptarc` archives become partly inspectable with standard tools, which is
  useful when debugging a container that will not open.
- The block framing is fixed for us: 64 KiB stored blocks, three block types,
  byte-aligned stored payload. We cannot "improve" any of it.
- The shared construction in `Coding/` exists because of this decision. Deflate
  needs canonical Huffman over three alphabets (288, 30 and 19 symbols, with
  limits of 15 and 7 bits) and match finding at 3…258 bytes, while `HuffmanCodec`
  and `LzssCodec` had both hard-coded to their own shapes. Generalising them
  surfaced two real bugs: symbol indices stored in a `byte`, which truncates
  above 255, and an empty-alphabet path that indexed an empty heap — the distance
  alphabet is empty whenever a block has no matches.
- More work than the alternative, concentrated in table data rather than in
  algorithms.

### Alternatives

- **Our own simplified format.** Cheaper, and nothing outside the project would
  read it. Rejected: it throws away the only external oracle available, and
  calling it "Deflate" at the defence would be a stretch.
- **Wrap the output in zlib.** An extra header and an Adler-32 the container
  already duplicates with CRC-32. Rejected as redundant.
- **Match zlib's output byte for byte.** Would pin every heuristic zlib happens
  to use. Rejected: RFC 1951 compliance is the contract, not imitation.

---

## ADR-0014 — Plain `Microsoft.Extensions.DependencyInjection` in the GUI, not Prism

**Status:** Accepted · 2026-11-08

### Context

`MainWindow` created its view model with `new`, and the view model ran the
analysis through a private static method. Nothing could be swapped, so the view
model could not be tested without running every real codec. The obvious
candidate for dependency injection in a XAML application is Prism, which has an
Avalonia port (`Prism.Avalonia`).

### Decision

Use `Microsoft.Extensions.DependencyInjection` directly. `App.axaml.cs` is the
composition root: it registers the services, view models and windows and
resolves `MainWindow` from the container. Everything else receives its
dependencies through the constructor. The first injected service is
`ICodecMeasurement`, which took over the measuring loop from
`MainWindowViewModel`.

### Consequences

- `MainWindowViewModel` depends on an interface and can be tested with a fake
  measurement.
- One small Microsoft package that is independent of the Avalonia version.
- MVVM stays on `CommunityToolkit.Mvvm` alone.
- `MainWindow` keeps a parameterless constructor for the XAML previewer and the
  runtime loader. It builds the dependencies by hand and is never used by the
  application.
- No navigation, regions or modules. If the GUI grows several screens, routing
  has to be written by hand or this decision revisited.

### Alternatives

- **Prism.Avalonia** — the latest stable release (9.0.537.11130) targets
  Avalonia 11.1 while the app is on 12.1. It also brings its own MVVM types
  (`BindableBase`, `DelegateCommand`) alongside `CommunityToolkit.Mvvm`, and
  `Prism.Core` 9 is under the Prism Community License rather than MIT. Its
  regions and modules solve a problem a one-window app does not have.
- **Keep `new` in code-behind** — no dependency, but the view model stays
  untestable in isolation.
