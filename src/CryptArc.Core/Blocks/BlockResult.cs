namespace CryptArc.Core.Blocks;

/// <summary>What happened to one block in a <see cref="BlockCompressor"/> run.</summary>
/// <param name="Index">Zero-based block number.</param>
/// <param name="Offset">Byte offset of the block in the input.</param>
/// <param name="OriginalLength">Block length before compression.</param>
/// <param name="CompressedLength">
/// Block length after compression. Equal to <paramref name="OriginalLength"/> when the
/// block was stored.
/// </param>
/// <param name="Codec">
/// The codec the block ended up with: the requested or recommended one, or
/// <see cref="CodecId.Store"/> when that one could not beat the original size.
/// </param>
/// <param name="Verified">
/// True when verification was on and the block decompressed back to the original.
/// False when verification was off or failed.
/// </param>
public readonly record struct BlockResult(
    int Index,
    long Offset,
    int OriginalLength,
    int CompressedLength,
    CodecId Codec,
    bool Verified);
