using BenchmarkDotNet.Attributes;
using CryptArc.Core.Analysis;

namespace CryptArc.Bench.Benchmarks;

/// <summary>
/// Byte counting: one table against four interleaved tables. The naive loop stalls on
/// runs of equal bytes, where each increment waits for the previous store to the same
/// counter, so random and run-heavy input are both measured.
/// </summary>
[MemoryDiagnoser]
public class ByteHistogramBenchmarks
{
    private byte[] _data = null!;
    private readonly long[] _counts = new long[ByteHistogram.Size];

    [Params(1024 * 1024)]
    public int Size { get; set; }

    [Params("Random", "Runs")]
    public string Shape { get; set; } = "Random";

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(42);
        _data = new byte[Size];

        if (Shape == "Random")
        {
            random.NextBytes(_data);
            return;
        }

        // Long runs of one value: the worst case for a single table.
        for (int i = 0; i < Size;)
        {
            byte value = (byte)random.Next(256);
            int run = random.Next(64, 4096);
            for (int j = 0; j < run && i < Size; j++, i++)
                _data[i] = value;
        }
    }

    [Benchmark(Baseline = true)]
    public long Naive()
    {
        ByteHistogram.CountNaive(_data, _counts);
        return _counts[0];
    }

    [Benchmark]
    public long FourTables()
    {
        ByteHistogram.Count(_data, _counts);
        return _counts[0];
    }
}
