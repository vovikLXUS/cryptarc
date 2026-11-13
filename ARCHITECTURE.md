# Architecture & Abstract Data Types

How CryptArc is put together, which abstractions it is built on, and which data
structures each algorithm relies on.

Companion documents: [docs/container-format.md](docs/container-format.md) for
the on-disk format, [CLAUDE.md](CLAUDE.md) for the working rules.

---

## 1. Layers

```
        ┌──────────────────────┐   ┌──────────────────────┐
        │  CryptArc.App        │   │  CryptArc.Cli        │   presentation
        │  Avalonia + MVVM     │   │  argument parsing    │   (thin, no logic)
        └──────────┬───────────┘   └──────────┬───────────┘
                   │                          │
                   └────────────┬─────────────┘
                                ▼
        ┌────────────────────────────────────────────────┐
        │                CryptArc.Core                   │   domain
        ├───────────────┬──────────────┬─────────────────┤
        │   Analysis    │    Codecs    │    Container    │
        │  entropy,     │  ICodec      │  header, tables,│
        │  codec choice │  implement.  │  crypto, blocks │
        ├───────────────┼──────────────┼─────────────────┤
        │       IO      │  Transforms  │     Hashing     │   primitives
        │   BitWriter/  │  BWT, MTF,   │  Crc32, SHA-256 │
        │   BitReader   │     RLE0     │       use       │
        └───────────────┴──────────────┴─────────────────┘
                                ▲
                   ┌────────────┴─────────────┐
                   │                          │
        ┌──────────┴───────────┐   ┌──────────┴───────────┐
        │  CryptArc.Tests      │   │  CryptArc.Bench      │   verification
        │  correctness         │   │  performance         │
        └──────────────────────┘   └──────────────────────┘
```

**Dependency rule:** arrows point inwards only. `Core` knows nothing about
Avalonia, the console, the file dialog or the command line. Both front ends are
replaceable; the algorithms are not.

Practical consequence: every feature is testable and benchmarkable without a UI,
and the same `Core` binary serves the GUI and the CLI.

---

## 2. Projects

| Project | Kind | Depends on | Responsibility |
|---|---|---|---|
| `CryptArc.Core` | class library | Argon2 (NuGet) | everything that matters |
| `CryptArc.Cli` | console exe | Core | parse arguments, print tables |
| `CryptArc.App` | Avalonia exe | Core, CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection, Semi.Avalonia, ScottPlot | window, file picker, presentation |
| `CryptArc.Bench` | console exe | Core, BenchmarkDotNet | measure |
| `CryptArc.Tests` | xUnit | Core | prove correctness |

---

## 3. Core abstract data types

### 3.1 `ICodec` — the central abstraction

```csharp
interface ICodec
{
    CodecId Id { get; }
    string  Name { get; }
    int MaxCompressedSize(int sourceLength);
    int Encode(ReadOnlySpan<byte> source, Span<byte> destination);  // -1 = did not fit
    int Decode(ReadOnlySpan<byte> source, Span<byte> destination);
}
```

Every algorithm — RLE, Huffman, LZSS, BWT, arithmetic coding — is reduced to
this one shape. Consequences:

- the container stores a `CodecId` per block and does not care what it means;
- the analyzer chooses an algorithm without knowing any of them;
- one test theory covers all of them;
- the benchmark parameterises over them.

**Contract details that matter:**

| Aspect | Rule | Why |
|---|---|---|
| `Encode` failure | returns `-1`, does not throw | "did not fit" is normal; caller falls back to `Store` |
| `Decode` failure | throws `InvalidDataException` | malformed input is an error, and decoders parse untrusted data |
| State | none; instances are immutable | blocks compress in parallel |
| `MaxCompressedSize` | must be a true upper bound | callers size buffers from it; underestimating corrupts output |

### 3.2 `CodecId` — persisted enum

A `byte` written into every archive. **Append-only**: renumbering a member
silently misreads every archive produced before the change. This is why the
container also carries a `formatVersion`.

### 3.3 `CodecRegistry` — the extension point

Maps `CodecId` to `ICodec` over the codecs that actually exist.
`TryGet` returns `false` for planned-but-unwritten algorithms, which let
`DataAnalyzer` recommend `Bwt` before `BwtCodec` existed — and still lets it
name codecs nobody has written yet. Callers fall back to `Store` instead of
crashing.

### 3.4 `Coding` — construction shared by the codecs

`Codecs/` holds algorithms; `Coding/` holds the machinery more than one of them
needs, and nothing in it implements `ICodec`:

| Type | What it builds |
|---|---|
| `CanonicalHuffman` | code lengths (heap, then package-merge when limited), canonical codes, decode tables, and `IsDecodable` to refuse an over-subscribed set read from a stream |
| `MatchFinder` | `LinearScanFinder` and `HashChainFinder` over a parameterised window and match range |
| `MatchLengths` | common-prefix length, 8 bytes at a time and byte by byte |
| `LzOutput` | expanding a back-reference, including the overlapping case |

Everything here takes its alphabet size from the span lengths and its limits as
parameters, because the codecs need different shapes: `HuffmanCodec` codes 256
byte values with codes of up to 15 bits, while Deflate needs 288 literal/length
symbols, 30 distance symbols and a 19-symbol alphabet limited to 7 bits, and
matches of 3-258 bytes where LZSS wants 4-131.

`BwtCodec` composes at the `ICodec` level instead — it hands its transformed
bytes to a `HuffmanCodec` and treats the result as an opaque stream. Deflate
cannot: it interleaves two alphabets into one bit stream and describes them with
a third, so it needs the construction rather than a codec.

### 3.5 `DataProfile` — an immutable value

```csharp
readonly record struct DataProfile(
    int SampledBytes, double Entropy, double RepeatRatio, double TextRatio);
```

A `readonly record struct`: small, copied by value, no allocation, free equality.

### 3.6 `BitWriter` / `BitReader` — `ref struct`

Bit-level streams over a caller-supplied `Span<byte>`. Declared `ref struct` so
the compiler *forbids* boxing them, storing them in fields or capturing them in
lambdas — the buffer they point at may live on the stack. Bit order is
least-significant-first throughout; encoder and decoder must agree or the output
is silently garbage.

---

## 4. Data structures per algorithm

| Algorithm | Structure | Operation | Complexity |
|---|---|---|---|
| RLE | none | single scan | `O(n)` time, `O(1)` space |
| Huffman — build | binary min-heap over 256 symbols | extract-min, insert | `O(k log k)`, k <= 256 |
| Huffman — encode | code/length table `uint[256]` | array index | `O(n)` |
| Huffman — decode | multi-level lookup table | one index per symbol | `O(n)`, vs `O(n*L)` for a tree walk |
| Huffman — length limit | package-merge | — | `O(k*L)` |
| LZSS — naive | none | scan the whole window | `O(n*W)` |
| LZSS — optimised | hash table plus chained positions (`head[]`, `prev[]`) | 3-byte hash, bounded chain walk | `O(n*k)`, k = `maxChain` |
| LZW | open-addressing hash table (own, not `Dictionary<>`) | insert / probe | `O(n)` amortised |
| Deflate — parse | token buffer, 4 bytes per input byte, pooled | append | `O(n)` |
| Deflate — block choice | frequency vectors, priced as dot products | compare three costs | `O(k)`, k <= 318 |
| Deflate — code lengths | 19-symbol alphabet with run-length repeats | encode / expand | `O(k)` |
| BWT — naive | comparison sort of suffixes | — | `O(n^2 log n)` |
| BWT — optimised | suffix array via prefix doubling plus radix sort | — | `O(n log n)` |
| BWT — inverse | LF-mapping over a counting table | — | `O(n)` |
| RLE0 | none | zero runs as bijective base-2 digits | `O(n)` |
| MTF | 256-byte array, not a linked list | move-to-front | `O(k)` worst, `O(1)` typical |
| Arithmetic coding | frequency model plus 32-bit range state | renormalise | `O(n)` |
| Block pipeline | fixed batch of pooled block buffers, twice the thread count | fill, seal in parallel, write in order | `O(1)` per block |
| Deduplication (planned, not implemented) | Rabin-Karp rolling hash, SHA-256 per chunk | content-defined chunking | `O(n)` |
| Analyzer | `stackalloc int[256]` histogram | count, then entropy | `O(n)`, zero allocation |

---

## 5. The block pipeline

Input is cut into fixed-size blocks (256 KiB). Each block is independent:

```
 read ──► analyze ──► compress ──► encrypt ──► write
 (I/O)     (CPU)       (CPU)        (CPU)      (I/O)
           └──────── parallel across blocks ────────┘
```

Independence buys three things at once:

1. **Parallelism** — `Parallel.For` over blocks; the speedup curve against
   thread count is the headline measurement of the optimisation section.
2. **Bounded memory** — a 20 GB file never needs more than a few blocks in RAM.
3. **Selective extraction** — one file can be restored without touching the rest
   of the archive, because the block index records where each file starts.

Blocks are read in batches of twice the thread count, sealed in parallel and
written in order before the next batch is read, so a slow disk cannot let
compressed blocks pile up without limit. Extraction is sequential.

---

## 6. Codec selection (the smart part)

`DataAnalyzer.Analyze` makes **one pass** over at most 64 KiB and returns a
`DataProfile`. `DataAnalyzer.Recommend` maps it to a `CodecId`:

| Condition | Codec | Rationale |
|---|---|---|
| entropy >= 7.8 bits/byte | `Store` | already compressed or encrypted — trying again only burns CPU |
| repeat ratio >= 30 % | `Rle` | long identical stretches |
| text ratio >= 90 % and entropy <= 6 | `Bwt` | block sorting wins on natural language and source code |
| otherwise | `Deflate` | general purpose |

Choosing by measurement rather than by trying every codec keeps the cost at one
cheap pass instead of k full compressions. The thresholds are plain constants
today; replacing them with a trained decision tree is a planned follow-up and
would not change any other part of the system.

If the chosen codec fails to shrink a block, the block is stored verbatim. The
archive therefore can never be meaningfully larger than its input.

---

## 7. Security architecture

```
password ──Argon2id(salt, t, m, p=4)──► masterKey (32 B)
                                          ├─HKDF("cryptarc:content:v1")─► contentKey
                                          └─HKDF("cryptarc:mac:v1")────► macKey

block ──compress──► AES-256-GCM(contentKey, random nonce) ──► nonce | ct | tag
archive ──────────► HMAC-SHA256(macKey) over everything ──► trailer
```

- Separate keys for confidentiality and authentication — never the same bytes.
- A fresh 96-bit nonce per block; reuse under one key breaks GCM completely.
- The header stays in plaintext (the reader needs the KDF parameters before it
  has a key) but is covered by the trailer HMAC, so its parameters cannot be
  downgraded silently.
- Order is **compress, then encrypt**. Reversing it costs the entire ratio.

---

## 8. Error handling contract

| Situation | Behaviour |
|---|---|
| output buffer too small during encode | `Encode` returns `-1`; caller stores the block |
| truncated or malformed compressed stream | `InvalidDataException` |
| unknown `CodecId` in an archive | `NotSupportedException` naming the codec |
| wrong password or tampered data | GCM tag check fails, `CryptographicException`; never a partial, silent restore |
| unsupported `formatVersion` | refuse to read, name the version |

Decoders parse attacker-controlled bytes. Every length read from a stream is
bounds-checked before it is used.

---

## 9. Where the optimisation work lives

Each row is a measurable before/after pair, kept side by side in the code:

| Component | Baseline | Optimised | Expected |
|---|---|---|---|
| CRC-32 | `ComputeNaive`, per bit | 256-entry table | ~8x |
| Bit writer | one branch per bit | 64-bit accumulator | ~8-10x |
| Huffman decode | tree walk | lookup table | ~5-8x |
| LZSS match search | linear window scan | hash chains plus lazy matching | 10-50x |
| LZSS match length | byte-by-byte compare | 8-byte compare plus `TrailingZeroCount` | ~3-5x |
| Deflate match search | linear window scan | hash chains plus lazy matching | see the note below |
| Deflate block type | fixed codes only | priced against dynamic and stored, cheapest wins | ratio, not speed |
| Back-reference copy | one byte at a time | doubling blocks, vectorised | 1.5-10x overlapping, 32x not |
| Suffix array | comparison sort | prefix doubling | `O(n^2 log n)` to `O(n log n)` |
| Whole archive | single thread | block-parallel pipeline | ~cores, per Amdahl |

**Measured, not expected.** The match-search row is the one place where the
first measurements contradicted the estimate above, and the honest answer
depends on the data:

| 64 KiB of | Linear scan | Hash chains | Ratio |
|---|---|---|---|
| random bytes | 1 466 895 us | 970 us | 1523x faster |
| runs | 10 695 us | 273 us | 39x faster |
| repeating text | 36 us | 111 us | **3x slower** |

Random data has no matches at all, so the scan pays the full `O(n*W)` to find
nothing while a chain lookup lands in an empty bucket — hence a speedup far
beyond the 10-50x this table first claimed. Repeating text is the opposite: the
scan finds a maximal 258-byte match on its 45th candidate and stops, while the
chains still have to hash and link every one of the 258 positions the match
skips over. zlib keeps those insertions too, because dropping them costs ratio,
so this is the cost of being correct rather than something to fix. It belongs in
the report as it stands: an optimisation that is worth 1523x on one input and
0.33x on another is a more useful result than an average.

---

## 10. Extending the system

Adding an algorithm touches exactly three places:

```
src/CryptArc.Core/Codecs/YourCodec.cs   implement ICodec
src/CryptArc.Core/CodecId.cs            append a value (never renumber)
src/CryptArc.Core/CodecRegistry.cs      register the instance
```

Tests, CLI output, the GUI comparison table and the benchmark parameters all
enumerate `CodecRegistry` and pick it up with no further changes. That is the
whole reason the registry exists.
