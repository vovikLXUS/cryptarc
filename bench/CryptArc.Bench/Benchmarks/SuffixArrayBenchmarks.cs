using BenchmarkDotNet.Attributes;
using CryptArc.Core.Transforms;

namespace CryptArc.Bench.Benchmarks;

/// <summary>
/// The BWT sorting story in the report: replacing a comparison sort of suffixes with
/// prefix doubling. The naive sort is data dependent — almost linear-logarithmic on
/// random bytes, where comparisons stop at the first byte, and quadratic on text,
/// where suffixes share long prefixes — so both shapes are measured.
/// </summary>
[MemoryDiagnoser]
public class SuffixArrayBenchmarks
{
    private byte[] _text = null!;
    private int[] _suffixArray = null!;

    [Params(16 * 1024, 64 * 1024)]
    public int Size { get; set; }

    [ParamsAllValues]
    public DataShape Shape { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _text = new byte[Size];
        _suffixArray = new int[Size];

        switch (Shape)
        {
            case DataShape.Random:
                new Random(2024).NextBytes(_text);
                break;

            case DataShape.Text:
                const string source = "the quick brown fox jumps over the lazy dog; ";
                for (int i = 0; i < Size; i++)
                    _text[i] = (byte)source[i % source.Length];
                break;
        }
    }

    [Benchmark(Baseline = true)]
    public int ComparisonSort()
    {
        SuffixArray.BuildNaive(_text, _suffixArray);
        return _suffixArray[0];
    }

    [Benchmark]
    public int PrefixDoubling()
    {
        SuffixArray.Build(_text, _suffixArray);
        return _suffixArray[0];
    }

    public enum DataShape
    {
        Random,
        Text,
    }
}
