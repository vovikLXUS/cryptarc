using CryptArc.Core.Blocks;

namespace CryptArc.Core.Container;

/// <summary>Settings for <see cref="ArchiveWriter"/>.</summary>
public sealed record ArchiveOptions
{
    /// <summary>
    /// Uncompressed block size in bytes, between <see cref="BlockCompressor.MinBlockSize"/>
    /// and <see cref="BlockCompressor.MaxBlockSize"/>. Defaults to 256 KiB.
    /// </summary>
    public int BlockSize { get; init; } = BlockCompressor.DefaultBlockSize;

    /// <summary>
    /// Worker threads per batch of blocks. One selects the sequential path, which is
    /// the naive baseline for hard rule 4. Defaults to the number of logical processors.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>The codec for every block, or null to pick one per block from its statistics.</summary>
    public CodecId? Codec { get; init; }

    /// <summary>
    /// Decompress each block before encrypting it and store it verbatim if the codec
    /// did not reproduce it. Roughly doubles the compression cost; on by default,
    /// because a codec bug found at extraction time can no longer be repaired.
    /// </summary>
    public bool Verify { get; init; } = true;

    /// <summary>Argon2id time cost. Defaults to 3 passes.</summary>
    public uint KdfIterations { get; init; } = 3;

    /// <summary>Argon2id memory cost in KiB. Defaults to 64 MiB.</summary>
    public uint KdfMemoryKiB { get; init; } = 64 * 1024;
}
