# Container

The encrypted `.cryptarc` archive. The byte layout is in
[docs/container-format.md](../../../docs/container-format.md).

| Type | Role |
|---|---|
| `ArchiveWriter` | creates an archive; one thread is the naive baseline |
| `ArchiveReader` | checks the HMAC on opening, then lists, extracts or verifies |
| `ArchiveSource` | a file to add, and the listing of a path on disk |
| `ArchiveEntry` | a stored file |
| `ArchiveOptions` | block size, threads, codec, verification, Argon2 costs |
| `ArchiveKeys` | Argon2id, then HKDF into the content and MAC keys |
| `ArchiveFormat` | header and table layout, limits, path safety |

Blocks are read in batches of twice the thread count and written in order, so
memory stays bounded. Extraction is sequential.
