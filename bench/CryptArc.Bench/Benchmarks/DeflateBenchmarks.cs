using System.IO.Compression;
using BenchmarkDotNet.Attributes;
using CryptArc.Core.Codecs;

namespace CryptArc.Bench.Benchmarks;

/// <summary>
/// Deflate's match search: a linear scan of the 32 KiB window against hash chains, the
/// same optimisation pair LZSS has but over Deflate's 3…258 byte matches.
/// </summary>
/// <remarks>
/// The exhaustive variant produces exactly the naive output, so its ratio column separates
/// what the chains cost in accuracy from what the chain limit and lazy matching cost.
/// </remarks>
[MemoryDiagnoser]
public class DeflateMatchSearchBenchmarks
{
    private byte[] _input = null!;
    private byte[] _output = null!;
    private readonly DeflateCodec _hashChains = new();
    private readonly DeflateCodec _hashChainsExhaustive = new(int.MaxValue, lazyMatching: false);

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
    public int LinearScan() => DeflateCodec.EncodeNaive(_input, _output);

    [Benchmark]
    public int HashChainsExhaustive() => _hashChainsExhaustive.Encode(_input, _output);

    [Benchmark]
    public int HashChainsLazy() => _hashChains.Encode(_input, _output);
}

/// <summary>
/// Our Deflate against <see cref="DeflateStream"/>: the one codec in this project whose
/// output a reference implementation can be measured against directly, in ratio as well as
/// in speed.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place a ready-made compressor may appear — see hard rule 1. The point
/// is calibration: a hand-written Deflate that is within a small factor of zlib on ratio is
/// doing the algorithm properly, and the speed gap says how much of the remaining distance
/// is engineering rather than algorithm.
/// </para>
/// <para>
/// Decoding is the fairer speed comparison of the two, because both sides are doing the
/// same table-driven work, while zlib's encoder carries years of tuning our match search
/// does not.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class DeflateVersusReferenceBenchmarks
{
    private byte[] _input = null!;
    private byte[] _output = null!;
    private byte[] _restored = null!;
    private byte[] _ours = null!;
    private byte[] _reference = null!;
    private readonly DeflateCodec _codec = new();

    [Params(64 * 1024)]
    public int Size { get; set; }

    [ParamsAllValues]
    public CodecBenchmarks.DataShape Shape { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _input = CodecBenchmarks.Generate(Shape, Size);
        _output = new byte[_codec.MaxCompressedSize(Size)];
        _restored = new byte[Size];

        int length = _codec.Encode(_input, _output);
        _ours = _output[..length];
        _reference = ReferenceDeflate(_input);
    }

    [Benchmark(Baseline = true)]
    public int ReferenceEncode() => ReferenceDeflate(_input).Length;

    [Benchmark]
    public int OurEncode() => _codec.Encode(_input, _output);

    [Benchmark]
    public int ReferenceDecodeOurStream() => ReferenceInflate(_ours);

    [Benchmark]
    public int OurDecodeOurStream() => _codec.Decode(_ours, _restored);

    [Benchmark]
    public int OurDecodeReferenceStream() => _codec.Decode(_reference, _restored);

    private static byte[] ReferenceDeflate(byte[] data)
    {
        using var output = new MemoryStream();

        using (var deflater = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            deflater.Write(data, 0, data.Length);

        return output.ToArray();
    }

    private static int ReferenceInflate(byte[] deflated)
    {
        using var input = new MemoryStream(deflated);
        using var inflater = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();

        inflater.CopyTo(output);
        return (int)output.Length;
    }
}
