using BenchmarkDotNet.Attributes;
using CryptArc.Core.Hashing;

namespace CryptArc.Bench.Benchmarks;

/// <summary>
/// The first optimisation story in the report: replacing the per-bit CRC loop
/// with a 256-entry lookup table.
/// </summary>
[MemoryDiagnoser]
public class Crc32Benchmarks
{
    private byte[] _data = null!;

    [Params(4096, 1024 * 1024)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[Size];
        new Random(42).NextBytes(_data);
    }

    [Benchmark(Baseline = true)]
    public uint Naive() => Crc32.ComputeNaive(_data);

    [Benchmark]
    public uint TableDriven() => Crc32.Compute(_data);
}
