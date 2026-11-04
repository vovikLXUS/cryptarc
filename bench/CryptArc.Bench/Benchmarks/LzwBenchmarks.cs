using BenchmarkDotNet.Attributes;
using CryptArc.Core.Codecs;

namespace CryptArc.Bench.Benchmarks;

/// <summary>
/// Measures the performance speedup of the open-addressing hash table LZW implementation
/// against the straightforward linear-search naive baseline.
/// </summary>
[MemoryDiagnoser]
public class LzwBenchmarks
{
    private readonly LzwCodec _codec = new();
    private byte[] _data = null!;
    private byte[] _compressed = null!;
    private byte[] _restored = null!;
    private int _compressedLength;

    [Params(4096, 64 * 1024)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        const string source = "the quick brown fox jumps over the lazy dog; lzw dictionary compression benchmark data; ";
        _data = new byte[Size];
        for (int i = 0; i < Size; i++)
            _data[i] = (byte)source[i % source.Length];

        _compressed = new byte[_codec.MaxCompressedSize(Size)];
        _restored = new byte[Size];
        _compressedLength = _codec.Encode(_data, _compressed);
    }

    [Benchmark(Baseline = true)]
    public int EncodeNaive() => _codec.EncodeNaive(_data, _compressed);

    [Benchmark]
    public int EncodeOptimised() => _codec.Encode(_data, _compressed);

    [Benchmark]
    public int DecodeNaive() => _codec.DecodeNaive(_compressed.AsSpan(0, _compressedLength), _restored);

    [Benchmark]
    public int DecodeOptimised() => _codec.Decode(_compressed.AsSpan(0, _compressedLength), _restored);
}
