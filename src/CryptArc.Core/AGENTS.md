# CryptArc.Core

## Rules specific to this project

- **No UI dependencies.** Avalonia, `Console`, dialogs and `Environment.Exit`
  do not belong here. Report problems by throwing or by returning a value.
- **No ready-made compressors.** `System.IO.Compression` and friends are banned
  in `src/`. They may appear in `bench/` as a baseline only.
- **No hand-written cryptography.** Use `System.Security.Cryptography` and
  Konscious Argon2. Do not implement a cipher, hash or KDF.
- **Hot paths allocate nothing.** `Span<T>` / `ReadOnlySpan<T>`,
  `stackalloc` for small fixed buffers, `ArrayPool<byte>.Shared` for blocks.
  No LINQ, no `List<T>`, no string formatting inside codec loops.
- **Everything public is XML-documented**, including the stream format of each
  codec — a codec whose format is not written down cannot be reviewed.
- Codecs are **stateless and thread-safe**; blocks are compressed in parallel.

## Adding a codec

`Codecs/YourCodec.cs` implementing `ICodec`, then append to `CodecId`, then
register in `CodecRegistry`. Nothing else needs editing — see
`Codecs/AGENTS.md` for the details and the contract.
