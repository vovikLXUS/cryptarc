using BenchmarkDotNet.Attributes;
using CryptArc.Core;

namespace CryptArc.Bench.Benchmarks;

/// <summary>
/// Throughput and compression ratio of every registered codec, over data shapes
/// that exercise their best and worst cases. This is the table that goes into the
/// comparison section of the report.
/// </summary>
[MemoryDiagnoser]
public class CodecBenchmarks
{
    private const int Size = 1024 * 1024;

    private byte[] _input = null!;
    private byte[] _compressed = null!;
    private byte[] _restored = null!;
    private int _compressedLength;
    private ICodec _codec = null!;

    [ParamsAllValues]
    public DataShape Shape { get; set; }

    [Params(CodecId.Store, CodecId.Rle, CodecId.Lzw)]
    public CodecId Codec { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _codec = CodecRegistry.Get(Codec);
        _input = Generate(Shape, Size);
        _compressed = new byte[_codec.MaxCompressedSize(_input.Length)];
        _restored = new byte[_input.Length];
        _compressedLength = _codec.Encode(_input, _compressed);
    }

    /// <summary>Compression ratio for the current parameter set, printed as a custom column.</summary>
    public double Ratio => _compressedLength / (double)_input.Length;

    [Benchmark]
    public int Encode() => _codec.Encode(_input, _compressed);

    [Benchmark]
    public int Decode() => _codec.Decode(_compressed.AsSpan(0, _compressedLength), _restored);

    private static byte[] Generate(DataShape shape, int size)
    {
        var random = new Random(2024);
        byte[] buffer = new byte[size];

        switch (shape)
        {
            case DataShape.Random:
                random.NextBytes(buffer);
                break;

            case DataShape.Runs:
                for (int i = 0; i < size;)
                {
                    byte value = (byte)random.Next(256);
                    int run = Math.Min(random.Next(4, 400), size - i);
                    buffer.AsSpan(i, run).Fill(value);
                    i += run;
                }
                break;

            case DataShape.Text:
                const string source = "the quick brown fox jumps over the lazy dog; ";
                for (int i = 0; i < size; i++)
                    buffer[i] = (byte)source[i % source.Length];
                break;
        }

        return buffer;
    }

    public enum DataShape
    {
        Random,
        Runs,
        Text,
    }
}
