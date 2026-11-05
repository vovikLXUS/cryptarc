using BenchmarkDotNet.Attributes;
using CryptArc.Core.Transforms;

namespace CryptArc.Bench.Benchmarks;

/// <summary>
/// Move-to-front: a front check, a vectorised search and a block move for long shifts,
/// against byte-by-byte loops.
/// Measured on BWT output of text, where most indices are small, and on random bytes,
/// where the average index is about 128 and the list operations dominate.
/// </summary>
[MemoryDiagnoser]
public class MoveToFrontBenchmarks
{
    private const int Size = 256 * 1024;

    private byte[] _input = null!;
    private byte[] _work = null!;

    [ParamsAllValues]
    public DataShape Shape { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _input = new byte[Size];
        _work = new byte[Size];

        switch (Shape)
        {
            case DataShape.Random:
                new Random(2024).NextBytes(_input);
                break;

            case DataShape.BwtText:
                const string source = "the quick brown fox jumps over the lazy dog; ";
                byte[] text = new byte[Size];
                for (int i = 0; i < Size; i++)
                    text[i] = (byte)source[i % source.Length];
                BurrowsWheeler.Forward(text, _input);
                break;
        }
    }

    [Benchmark(Baseline = true)]
    public byte PlainLoop()
    {
        // The transform works in place, so each call starts from a fresh copy. The copy
        // is a 256 KiB memmove, the same in both methods and small next to the work.
        _input.CopyTo(_work, 0);
        MoveToFront.EncodeNaive(_work);
        return _work[^1];
    }

    [Benchmark]
    public byte Optimised()
    {
        _input.CopyTo(_work, 0);
        MoveToFront.Encode(_work);
        return _work[^1];
    }

    public enum DataShape
    {
        Random,
        BwtText,
    }
}
