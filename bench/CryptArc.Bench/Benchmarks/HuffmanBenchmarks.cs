using BenchmarkDotNet.Attributes;
using CryptArc.Core.Codecs;

namespace CryptArc.Bench.Benchmarks;

/// <summary>
/// The Huffman decoding story in the report: replacing the bit-by-bit tree walk with
/// a two-level lookup table.
/// </summary>
[MemoryDiagnoser]
public class HuffmanBenchmarks
{
    private const int Size = 1024 * 1024;

    private readonly HuffmanCodec _codec = new();

    private byte[] _compressed = null!;
    private byte[] _restored = null!;
    private int _compressedLength;

    [ParamsAllValues]
    public DataShape Shape { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        byte[] input = Generate(Shape);

        _compressed = new byte[_codec.MaxCompressedSize(input.Length)];
        _restored = new byte[input.Length];
        _compressedLength = _codec.Encode(input, _compressed);
    }

    [Benchmark(Baseline = true)]
    public int TreeWalk() => HuffmanCodec.DecodeTreeWalk(_compressed.AsSpan(0, _compressedLength), _restored);

    [Benchmark]
    public int LookupTable() => _codec.Decode(_compressed.AsSpan(0, _compressedLength), _restored);

    private static byte[] Generate(DataShape shape)
    {
        byte[] buffer = new byte[Size];

        switch (shape)
        {
            case DataShape.Random:
                new Random(2024).NextBytes(buffer);
                break;

            case DataShape.Text:
                const string source = "the quick brown fox jumps over the lazy dog; ";
                for (int i = 0; i < Size; i++)
                    buffer[i] = (byte)source[i % source.Length];
                break;
        }

        return buffer;
    }

    public enum DataShape
    {
        Random,
        Text,
    }
}
