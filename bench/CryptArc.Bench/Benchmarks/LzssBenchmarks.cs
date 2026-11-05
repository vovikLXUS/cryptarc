using BenchmarkDotNet.Attributes;
using CryptArc.Core.Codecs;

namespace CryptArc.Bench.Benchmarks;

/// <summary>
/// LZSS match search: a linear scan of the 32 KiB window against hash chains.
/// ARCHITECTURE.md §9 expects 10-50x from the chains. The greedy variant with an unbounded
/// chain shows how much of that comes from the chain limit and lazy matching, since it
/// produces exactly the naive output.
/// </summary>
[MemoryDiagnoser]
public class LzssMatchSearchBenchmarks
{
    private byte[] _input = null!;
    private byte[] _output = null!;
    private readonly LzssCodec _hashChains = new();
    private readonly LzssCodec _hashChainsExhaustive = new(int.MaxValue, lazyMatching: false);

    [Params(16 * 1024, 64 * 1024)]
    public int Size { get; set; }

    [ParamsAllValues]
    public CodecBenchmarks.DataShape Shape { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _input = CodecBenchmarks.Generate(Shape, Size);
        _output = new byte[_hashChains.MaxCompressedSize(Size)];
    }

    [Benchmark(Baseline = true)]
    public int LinearScan() => LzssCodec.EncodeNaive(_input, _output);

    [Benchmark]
    public int HashChainsExhaustive() => _hashChainsExhaustive.Encode(_input, _output);

    [Benchmark]
    public int HashChainsLazy() => _hashChains.Encode(_input, _output);
}

/// <summary>
/// LZSS match length: one byte per step against 8 bytes per step with
/// <c>TrailingZeroCount</c>. ARCHITECTURE.md §9 expects about 3-5x on long matches.
/// </summary>
[MemoryDiagnoser]
public class LzssMatchLengthBenchmarks
{
    private byte[] _left = null!;
    private byte[] _right = null!;

    /// <summary>Length of the common prefix. 131 is the longest match LZSS can emit.</summary>
    [Params(8, 32, LzssCodec.MaxMatch)]
    public int Length { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _left = new byte[LzssCodec.MaxMatch + 1];
        new Random(7).NextBytes(_left);
        _right = (byte[])_left.Clone();
        _right[Length] ^= 0xFF;
    }

    [Benchmark(Baseline = true)]
    public int Bytewise() => LzssCodec.MatchLengthNaive(_left, _right, _left.Length);

    [Benchmark]
    public int EightBytesAtATime() => LzssCodec.MatchLength(_left, _right, _left.Length);
}
