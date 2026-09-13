namespace CryptArc.Core;

/// <summary>
/// Identifies the compression algorithm used for a single block.
/// The value is persisted in the archive container, so existing members
/// must never be renumbered — only append new ones.
/// </summary>
public enum CodecId : byte
{
    Store = 0,
    Rle = 1,
    Huffman = 2,
    Lzss = 3,
    Deflate = 4,
    Lzw = 5,
    Bwt = 6,
    Arithmetic = 7,
}
