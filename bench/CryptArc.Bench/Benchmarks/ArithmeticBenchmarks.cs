using BenchmarkDotNet.Attributes;
using CryptArc.Core.Codecs;

namespace CryptArc.Bench.Benchmarks;

/// <summary>
/// The Arithmetic coding optimization story in the report: replacing the O(k) linear cumulative sum
/// scans and symbol search with an O(log k) Fenwick tree (Binary Indexed Tree) and binary lifting.
/// </summary>
[MemoryDiagnoser]
public class ArithmeticBenchmarks
{
    private const int Size = 128 * 1024;

    private readonly ArithmeticCodec _codec = new();

    private byte[] _input = null!;
    private byte[] _compressed = null!;
    private byte[] _restored = null!;
    private int _compressedLength;

    [Params(CodecBenchmarks.DataShape.Text, CodecBenchmarks.DataShape.Random)]
    public CodecBenchmarks.DataShape Shape { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _input = CodecBenchmarks.Generate(Shape, Size);
        _compressed = new byte[_codec.MaxCompressedSize(_input.Length)];
        _restored = new byte[_input.Length];
        _compressedLength = _codec.Encode(_input, _compressed);
    }

    [Benchmark(Baseline = true)]
    public int EncodeNaive() => _codec.EncodeNaive(_input, _compressed);

    [Benchmark]
    public int EncodeFenwick() => _codec.Encode(_input, _compressed);

    [Benchmark]
    public int DecodeNaive() => _codec.DecodeNaive(_compressed.AsSpan(0, _compressedLength), _restored);

    [Benchmark]
    public int DecodeFenwick() => _codec.Decode(_compressed.AsSpan(0, _compressedLength), _restored);
}

