namespace CryptArc.Core;

/// <summary>
/// Identifies the compression algorithm used for a single block.
/// The value is persisted in the archive container, so existing members
/// must never be renumbered — only append new ones.
/// </summary>
public enum CodecId : byte
{
    /// <summary>No compression: the block is copied verbatim.</summary>
    Store = 0,
    /// <summary>Run-length encoding, PackBits style.</summary>
    Rle = 1,
    /// <summary>Canonical Huffman coding.</summary>
    Huffman = 2,
    /// <summary>LZSS: sliding-window Lempel–Ziv with literal runs and back-references.</summary>
    Lzss = 3,
    /// <summary>Deflate: LZSS followed by Huffman coding.</summary>
    Deflate = 4,
    /// <summary>Lempel–Ziv–Welch with variable code width.</summary>
    Lzw = 5,
    /// <summary>Burrows–Wheeler transform, move-to-front, zero-run encoding and Huffman.</summary>
    Bwt = 6,
    /// <summary>Arithmetic (range) coding with an adaptive model.</summary>
    Arithmetic = 7,
}
