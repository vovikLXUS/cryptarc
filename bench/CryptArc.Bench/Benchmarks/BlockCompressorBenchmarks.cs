using BenchmarkDotNet.Attributes;
using CryptArc.Core;
using CryptArc.Core.Blocks;

namespace CryptArc.Bench.Benchmarks;

/// <summary>
/// Speedup of compressing independent blocks in parallel (ADR-0005) over the
/// one-block-after-another baseline, as the thread count grows. The input is
/// 16 MiB of mixed content so every thread has several blocks to work on.
/// </summary>
[MemoryDiagnoser]
public class BlockCompressorBenchmarks
{
    private byte[] _data = null!;
    private BlockCompressionOptions _options = null!;

    [Params(2, 4, 8)]
    public int Threads { get; set; }

    [Params(CodecId.Huffman, CodecId.Deflate)]
    public CodecId Codec { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // Half text-like, half random: realistic and fixed by seed.
        var random = new Random(42);
        _data = new byte[16 * 1024 * 1024];
        random.NextBytes(_data);
        for (int i = 0; i < _data.Length / 2; i++)
            _data[i] = (byte)(' ' + random.Next(64));

        _options = new BlockCompressionOptions
        {
            Codec = Codec,
            MaxDegreeOfParallelism = Threads,
            Verify = false,
        };
    }

    [Benchmark(Baseline = true)]
    public int Sequential() => BlockCompressor.CompressSequential(_data, _options).Length;

    [Benchmark]
    public int Parallel() => BlockCompressor.Compress(_data, _options).Length;
}
