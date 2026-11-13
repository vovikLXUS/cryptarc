# CryptArc.Tests

xUnit suite over `CryptArc.Core`.

| File | Covers |
|---|---|
| `Codecs/CodecRoundTripTests.cs` | the `ICodec` contract, for every registered codec |
| `Codecs/RleCodecTests.cs` | RLE specifics: group boundaries, expansion bound, malformed input |
| `IO/BitStreamTests.cs` | bit writer/reader round-trip, overflow, zero-extension |
| `Crc32Tests.cs` | standard check value, and table vs naive agreement |
| `DataAnalyzerTests.cs` | entropy extremes and the selection thresholds |
| `LocalizationResourcesTests.cs` | the GUI's `Strings.resx` files: same keys and placeholders in both languages, every key used in the app exists, no literal text in a view |
| `TestData.cs` | shared deterministic sample buffers |

## The central test

`CodecRoundTripTests` is a theory over `CodecRegistry.All` crossed with seven
input shapes: empty, single byte, constant, alternating, random, mixed and text.
Register a new codec and it is covered immediately — no new test file needed for
the basics.

It asserts three things:

1. `Decode(Encode(x)) == x`, byte for byte.
2. Output never exceeds `MaxCompressedSize`.
3. A too-small destination yields `-1` rather than an overrun.

## Running

```bash
dotnet test CryptArc.slnx -c Release
dotnet test --filter FullyQualifiedName~RleCodec
```
