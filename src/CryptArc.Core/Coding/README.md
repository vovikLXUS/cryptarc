# Coding

Coding machinery that more than one codec needs. Nothing here implements
`ICodec`: these are the shared building blocks the codecs are assembled from.

| File | Contents |
|---|---|
| `CanonicalHuffman.cs` | optimal code lengths, package-merge length limiting, canonical code assignment, two-level decode table, explicit decode tree |
| `MatchFinder.cs` | `Match`, `IMatchFinder`, and the two Lempel-Ziv match searches: linear scan and hash chains |
| `MatchLengths.cs` | common-prefix length of two sequences, 8 bytes at a time and byte by byte |

## Why it is a separate folder

`HuffmanCodec` needs canonical Huffman over 256 byte values. Deflate needs it
three times over, with different shapes: 288 literal/length symbols, 30 distance
symbols, and a 19-symbol alphabet describing the code lengths of the first two,
limited to 7 bits instead of 15.

`BwtCodec` gets away with composing at the `ICodec` level — it hands its
transformed bytes to a `HuffmanCodec` instance and treats the result as an opaque
stream. Deflate cannot: it interleaves two alphabets into a single bit stream and
describes them with a third, so it needs the code *construction*, not a codec.
Hence the alphabet size comes from the span lengths and the code-length limit is
a parameter.

## Reading order

1. `CanonicalHuffman.BuildLengths` — frequencies in, code lengths out.
2. `CanonicalHuffman.AssignCodes` — lengths in, bit-reversed codes out. The
   reversal is the part that trips people up; see the remarks on the class.
3. `CanonicalHuffman.BuildDecodeTable` — lengths in, lookup table out, plus
   `DecodeTableSize` to size the buffer.

## Match finding

`LzssCodec` searches with a 32 KiB window, matches of 4…131 bytes and a 3-byte
hash. Deflate uses the same window and hash but matches of 3…258 bytes, so the
window, the minimum and the maximum are constructor parameters rather than
constants. The two finders are an optimised/naive pair in the sense of hard
rule 4: with an unbounded chain and greedy matching they produce identical
output, which is what makes the fast one trustworthy.
