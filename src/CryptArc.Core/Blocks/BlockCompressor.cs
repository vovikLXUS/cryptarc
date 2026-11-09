using System.Buffers;
using CryptArc.Core.Analysis;

namespace CryptArc.Core.Blocks;

/// <summary>
/// Splits a buffer into fixed-size independent blocks and compresses each one,
/// optionally in parallel (ADR-0005).
/// </summary>
/// <remarks>
/// <para>
/// This produces per-block results, not an archive; <c>Container/</c> writes those.
/// Each block is compressed with
/// either one fixed codec or, in automatic mode, the codec
/// <see cref="DataAnalyzer.Recommend"/> picks for that block.
/// </para>
/// <para>
/// A block never grows. The destination buffer is exactly as long as the block, so
/// a codec that cannot beat the original returns -1 and the block is recorded as
/// <see cref="CodecId.Store"/> — the fallback the <see cref="ICodec"/> contract
/// describes.
/// </para>
/// <para>
/// <see cref="CompressSequential"/> is the naive counterpart kept for hard rule 4:
/// one thread, one block after another. <see cref="Compress"/> runs the same
/// per-block work under <see cref="Parallel.For(int, int, ParallelOptions, Action{int})"/>;
/// both must produce identical results.
/// </para>
/// </remarks>
public static class BlockCompressor
{
    /// <summary>Default block size, 256 KiB (ADR-0005).</summary>
    public const int DefaultBlockSize = 256 * 1024;

    /// <summary>Smallest accepted block size, 4 KiB.</summary>
    public const int MinBlockSize = 4 * 1024;

    /// <summary>Largest accepted block size, 64 MiB.</summary>
    public const int MaxBlockSize = 64 * 1024 * 1024;

    /// <summary>Number of blocks <paramref name="dataLength"/> bytes split into.</summary>
    /// <param name="dataLength">Input length in bytes.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public static int BlockCount(long dataLength, int blockSize) =>
        (int)((dataLength + blockSize - 1) / blockSize);

    /// <summary>
    /// Compresses every block of <paramref name="data"/> on up to
    /// <see cref="BlockCompressionOptions.MaxDegreeOfParallelism"/> threads.
    /// </summary>
    /// <param name="data">The whole input.</param>
    /// <param name="options">Block size, thread count, codec and verification.</param>
    /// <param name="progress">
    /// Receives each block's result as soon as that block is done — out of order when
    /// running in parallel. Called from worker threads; a <see cref="Progress{T}"/>
    /// created on the UI thread marshals it back.
    /// </param>
    /// <param name="cancellationToken">Stops the run between blocks.</param>
    /// <returns>One result per block, in block order.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    /// <exception cref="ArgumentException">The requested codec is not implemented.</exception>
    /// <exception cref="OperationCanceledException">The run was cancelled.</exception>
    public static BlockResult[] Compress(
        ReadOnlyMemory<byte> data,
        BlockCompressionOptions options,
        IProgress<BlockResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ICodec? fixedCodec = Validate(options);
        var results = new BlockResult[BlockCount(data.Length, options.BlockSize)];

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
            CancellationToken = cancellationToken,
        };

        Parallel.For(0, results.Length, parallelOptions, index =>
        {
            results[index] = CompressBlock(data, index, options, fixedCodec);
            progress?.Report(results[index]);
        });

        return results;
    }

    /// <summary>
    /// The naive counterpart of <see cref="Compress"/>: the same blocks, one after
    /// another on the calling thread. <see cref="BlockCompressionOptions.MaxDegreeOfParallelism"/>
    /// is ignored.
    /// </summary>
    /// <param name="data">The whole input.</param>
    /// <param name="options">Block size, codec and verification.</param>
    /// <param name="progress">Receives each block's result, in block order.</param>
    /// <param name="cancellationToken">Stops the run between blocks.</param>
    /// <returns>One result per block, in block order.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    /// <exception cref="ArgumentException">The requested codec is not implemented.</exception>
    /// <exception cref="OperationCanceledException">The run was cancelled.</exception>
    public static BlockResult[] CompressSequential(
        ReadOnlyMemory<byte> data,
        BlockCompressionOptions options,
        IProgress<BlockResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ICodec? fixedCodec = Validate(options);
        var results = new BlockResult[BlockCount(data.Length, options.BlockSize)];

        for (int index = 0; index < results.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results[index] = CompressBlock(data, index, options, fixedCodec);
            progress?.Report(results[index]);
        }

        return results;
    }

    private static ICodec? Validate(BlockCompressionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.BlockSize, MinBlockSize, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.BlockSize, MaxBlockSize, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxDegreeOfParallelism, 1, nameof(options));

        if (options.Codec is not { } id)
            return null;

        if (!CodecRegistry.TryGet(id, out ICodec codec))
            throw new ArgumentException($"Codec {id} is not implemented.", nameof(options));

        return codec;
    }

    private static BlockResult CompressBlock(
        ReadOnlyMemory<byte> data, int index, BlockCompressionOptions options, ICodec? fixedCodec)
    {
        long offset = (long)index * options.BlockSize;
        int length = (int)Math.Min(options.BlockSize, data.Length - offset);
        ReadOnlySpan<byte> block = data.Span.Slice((int)offset, length);

        ICodec codec = fixedCodec ?? ChooseCodec(block);

        byte[] compressed = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            int compressedLength = codec.Encode(block, compressed.AsSpan(0, length));

            // Did not fit in the original size: keep the block as it is.
            if (compressedLength < 0)
                return new BlockResult(index, offset, length, length, CodecId.Store, Verified: options.Verify);

            bool verified = options.Verify && RoundTrips(codec, block, compressed.AsSpan(0, compressedLength));
            return new BlockResult(index, offset, length, compressedLength, codec.Id, verified);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compressed);
        }
    }

    internal static ICodec ChooseCodec(ReadOnlySpan<byte> block)
    {
        CodecId recommended = DataAnalyzer.Recommend(DataAnalyzer.Analyze(block));
        return CodecRegistry.TryGet(recommended, out ICodec codec) ? codec : CodecRegistry.Get(CodecId.Store);
    }

    internal static bool RoundTrips(ICodec codec, ReadOnlySpan<byte> original, ReadOnlySpan<byte> compressed)
    {
        byte[] restored = ArrayPool<byte>.Shared.Rent(original.Length);
        try
        {
            int restoredLength = codec.Decode(compressed, restored.AsSpan(0, original.Length));
            return restoredLength == original.Length
                && original.SequenceEqual(restored.AsSpan(0, restoredLength));
        }
        catch (InvalidDataException)
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(restored);
        }
    }
}
