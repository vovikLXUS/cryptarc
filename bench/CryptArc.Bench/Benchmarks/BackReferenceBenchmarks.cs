using BenchmarkDotNet.Attributes;
using CryptArc.Core.Coding;

namespace CryptArc.Bench.Benchmarks;

/// <summary>
/// Back-reference expansion, the inner loop of every Lempel-Ziv decoder: one byte at a
/// time against blocks of <c>distance</c> bytes through a vectorised copy.
/// </summary>
/// <remarks>
/// <para>
/// The distances are the ones that actually occur. A distance of 1 is a run of one byte
/// and has its own <c>Fill</c> path. 45 is a repeating phrase, the common case in text and
/// the one that made this worth doing — it decoded at about 2 GB/s byte by byte. A
/// distance at or above the length does not overlap at all and is a single block move, so
/// it measures the ceiling.
/// </para>
/// <para>
/// This is what the length is for: expanding 258 bytes is the longest match Deflate can
/// emit, and 131 the longest LZSS can.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class BackReferenceBenchmarks
{
    private const int Prefix = 1024;

    private byte[] _buffer = null!;

    /// <summary>How far back the match starts. 1 is a byte run, 45 a repeating phrase, 258 no overlap.</summary>
    [Params(1, 3, 45, 258)]
    public int Distance { get; set; }

    /// <summary>How many bytes the match expands to. 258 is Deflate's maximum, 131 LZSS's.</summary>
    [Params(131, 258)]
    public int Length { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _buffer = new byte[Prefix + Length];
        new Random(17).NextBytes(_buffer.AsSpan(0, Prefix));
    }

    [Benchmark(Baseline = true)]
    public byte Bytewise()
    {
        LzOutput.CopyMatchNaive(_buffer, Prefix, Distance, Length);
        return _buffer[Prefix];
    }

    [Benchmark]
    public byte BlockAtATime()
    {
        LzOutput.CopyMatch(_buffer, Prefix, Distance, Length);
        return _buffer[Prefix];
    }
}
