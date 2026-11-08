namespace CryptArc.Core.Blocks;

/// <summary>Settings for <see cref="BlockCompressor"/>.</summary>
public sealed record BlockCompressionOptions
{
    /// <summary>
    /// Block size in bytes, between <see cref="BlockCompressor.MinBlockSize"/> and
    /// <see cref="BlockCompressor.MaxBlockSize"/>. Defaults to 256 KiB.
    /// </summary>
    public int BlockSize { get; init; } = BlockCompressor.DefaultBlockSize;

    /// <summary>
    /// Upper bound on worker threads for <see cref="BlockCompressor.Compress"/>.
    /// Defaults to the number of logical processors.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>
    /// The codec for every block, or null to let <see cref="Analysis.DataAnalyzer"/>
    /// pick one per block.
    /// </summary>
    public CodecId? Codec { get; init; }

    /// <summary>
    /// Decompress each block after compressing it and compare it with the original.
    /// Roughly doubles the cost; on by default.
    /// </summary>
    public bool Verify { get; init; } = true;
}
