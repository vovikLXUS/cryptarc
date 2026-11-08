using System.Collections.Concurrent;
using CryptArc.Core;
using CryptArc.Core.Analysis;
using CryptArc.Core.Blocks;

namespace CryptArc.Tests.Blocks;

public class BlockCompressorTests
{
    private const int Block = BlockCompressor.MinBlockSize;

    public static TheoryData<CodecId> RegisteredCodecs()
    {
        var data = new TheoryData<CodecId>();
        foreach (ICodec codec in CodecRegistry.All)
            data.Add(codec.Id);
        return data;
    }

    [Fact]
    public void EmptyInputHasNoBlocks()
    {
        Assert.Empty(BlockCompressor.Compress(Array.Empty<byte>(), new BlockCompressionOptions()));
        Assert.Empty(BlockCompressor.CompressSequential(Array.Empty<byte>(), new BlockCompressionOptions()));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(Block - 1, 1)]
    [InlineData(Block, 1)]
    [InlineData(Block + 1, 2)]
    [InlineData(3 * Block, 3)]
    public void BlocksCoverTheInputExactly(int length, int expectedBlocks)
    {
        byte[] data = TestData.Mixed(length);

        BlockResult[] blocks = BlockCompressor.Compress(data, new BlockCompressionOptions { BlockSize = Block });

        Assert.Equal(expectedBlocks, blocks.Length);
        long next = 0;
        for (int i = 0; i < blocks.Length; i++)
        {
            Assert.Equal(i, blocks[i].Index);
            Assert.Equal(next, blocks[i].Offset);
            next += blocks[i].OriginalLength;
        }
        Assert.Equal(length, next);
    }

    [Theory]
    [MemberData(nameof(RegisteredCodecs))]
    public void EveryBlockRoundTripsWithAFixedCodec(CodecId codec)
    {
        byte[] data = TestData.Mixed(5 * Block + 123);

        BlockResult[] blocks = BlockCompressor.Compress(data,
            new BlockCompressionOptions { BlockSize = Block, Codec = codec });

        Assert.All(blocks, block =>
        {
            Assert.True(block.Verified, $"Block {block.Index} did not round-trip with {codec}.");
            Assert.True(block.CompressedLength <= block.OriginalLength);
            Assert.True(block.Codec == codec || block.Codec == CodecId.Store);
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    public void ParallelAgreesWithSequential(int threads)
    {
        byte[] data = [.. TestData.Text(), .. TestData.Random(3 * Block), .. TestData.Mixed(4 * Block)];
        var options = new BlockCompressionOptions { BlockSize = Block, MaxDegreeOfParallelism = threads };

        Assert.Equal(
            BlockCompressor.CompressSequential(data, options),
            BlockCompressor.Compress(data, options));
    }

    [Fact]
    public void IncompressibleBlocksFallBackToStore()
    {
        byte[] data = TestData.Random(4 * Block);

        BlockResult[] blocks = BlockCompressor.Compress(data,
            new BlockCompressionOptions { BlockSize = Block, Codec = CodecId.Huffman });

        Assert.All(blocks, block =>
        {
            Assert.Equal(CodecId.Store, block.Codec);
            Assert.Equal(block.OriginalLength, block.CompressedLength);
        });
    }

    [Fact]
    public void AutomaticModeUsesTheAnalyzersChoicePerBlock()
    {
        byte[] data = [.. TestData.Text(), .. TestData.Random(2 * Block), .. TestData.Constant(2 * Block)];

        BlockResult[] blocks = BlockCompressor.Compress(data, new BlockCompressionOptions { BlockSize = Block });

        Assert.All(blocks, block =>
        {
            CodecId recommended = DataAnalyzer.Recommend(
                DataAnalyzer.Analyze(data.AsSpan((int)block.Offset, block.OriginalLength)));

            // The recommendation stands unless it could not beat the original size.
            Assert.True(block.Codec == recommended || block.Codec == CodecId.Store,
                $"Block {block.Index}: expected {recommended} or Store, got {block.Codec}.");
            Assert.True(block.Verified);
        });
        Assert.Contains(blocks, block => block.Codec != CodecId.Store);
    }

    [Fact]
    public void ProgressReportsEveryBlockOnce()
    {
        byte[] data = TestData.Mixed(7 * Block + 5);
        var seen = new ConcurrentBag<int>();

        BlockCompressor.Compress(data, new BlockCompressionOptions { BlockSize = Block },
            new SynchronousProgress(block => seen.Add(block.Index)));

        Assert.Equal(Enumerable.Range(0, 8), seen.Order());
    }

    [Fact]
    public void VerificationCanBeSwitchedOff()
    {
        BlockResult[] blocks = BlockCompressor.Compress(TestData.Mixed(2 * Block),
            new BlockCompressionOptions { BlockSize = Block, Verify = false });

        Assert.All(blocks, block => Assert.False(block.Verified));
    }

    [Fact]
    public void CancellationStopsBothVariants()
    {
        byte[] data = TestData.Mixed(4 * Block);
        var options = new BlockCompressionOptions { BlockSize = Block };
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            BlockCompressor.Compress(data, options, cancellationToken: cancelled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            BlockCompressor.CompressSequential(data, options, cancellationToken: cancelled.Token));
    }

    [Theory]
    [InlineData(BlockCompressor.MinBlockSize - 1, 1)]
    [InlineData(BlockCompressor.MaxBlockSize + 1, 1)]
    [InlineData(Block, 0)]
    public void OutOfRangeOptionsAreRejected(int blockSize, int threads)
    {
        var options = new BlockCompressionOptions { BlockSize = blockSize, MaxDegreeOfParallelism = threads };

        Assert.Throws<ArgumentOutOfRangeException>(() => BlockCompressor.Compress(new byte[10], options));
    }

    [Fact]
    public void AnUnimplementedCodecIsRejected()
    {
        var options = new BlockCompressionOptions { Codec = (CodecId)200 };

        Assert.Throws<ArgumentException>(() => BlockCompressor.Compress(new byte[10], options));
    }

    /// <summary>
    /// <see cref="Progress{T}"/> posts to a synchronization context and may run after
    /// the call returns; the test needs every report delivered before it asserts.
    /// </summary>
    private sealed class SynchronousProgress(Action<BlockResult> report) : IProgress<BlockResult>
    {
        public void Report(BlockResult value) => report(value);
    }
}
