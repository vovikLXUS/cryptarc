using System;
using System.Diagnostics;
using CryptArc.App.ViewModels;
using CryptArc.Core;
using CryptArc.Core.Analysis;
using CryptArc.Core.Hashing;

namespace CryptArc.App.Services;

/// <summary>
/// Default <see cref="ICodecMeasurement"/>: analyses the data, then compresses and
/// decompresses it with every codec in <see cref="CodecRegistry"/> and checks the
/// round trip by CRC-32.
/// </summary>
/// <remarks>Stateless, so it is registered as a singleton.</remarks>
public sealed class CodecMeasurement : ICodecMeasurement
{
    /// <summary>
    /// Upper bound on heatmap cells. A larger file is sampled at evenly spaced blocks,
    /// so the cost and the number of controls stay bounded on a multi-gigabyte input.
    /// </summary>
    public const int MaxHeatmapBlocks = 256;

    /// <inheritdoc />
    public MeasurementReport Measure(byte[] data)
    {
        DataProfile profile = DataAnalyzer.Analyze(data);
        BlockProfile[] blocks = ProfileBlocks(data);
        var histogram = new long[ByteHistogram.Size];
        ByteHistogram.Count(data, histogram);
        uint crc = Crc32.Compute(data);

        var results = new CodecResult[CodecRegistry.All.Count];

        for (int i = 0; i < results.Length; i++)
        {
            ICodec codec = CodecRegistry.All[i];
            byte[] compressed = new byte[codec.MaxCompressedSize(data.Length)];

            var encodeTimer = Stopwatch.StartNew();
            int compressedLength = codec.Encode(data, compressed);
            encodeTimer.Stop();

            if (compressedLength < 0)
            {
                results[i] = new CodecResult(codec.Name, 0, 1, 0, 0, RoundTripOk: false);
                continue;
            }

            byte[] restored = new byte[data.Length];

            var decodeTimer = Stopwatch.StartNew();
            int restoredLength = codec.Decode(compressed.AsSpan(0, compressedLength), restored);
            decodeTimer.Stop();

            bool ok = restoredLength == data.Length && Crc32.Compute(restored) == crc;

            results[i] = new CodecResult(
                codec.Name,
                compressedLength,
                data.Length == 0 ? 1 : compressedLength / (double)data.Length,
                Throughput(data.Length, encodeTimer.Elapsed),
                Throughput(data.Length, decodeTimer.Elapsed),
                ok);
        }

        return new MeasurementReport(profile, crc, results, blocks, histogram);
    }

    // Blocks are DataAnalyzer.SampleSize long, the same window the analyzer samples,
    // so each cell shows exactly what the per-block codec choice would see.
    private static BlockProfile[] ProfileBlocks(byte[] data)
    {
        const int blockSize = DataAnalyzer.SampleSize;
        int blockCount = (int)((data.LongLength + blockSize - 1) / blockSize);
        int cells = Math.Min(blockCount, MaxHeatmapBlocks);

        var blocks = new BlockProfile[cells];

        for (int i = 0; i < cells; i++)
        {
            long index = cells == blockCount ? i : (long)i * blockCount / cells;
            long offset = index * blockSize;
            int length = (int)Math.Min(blockSize, data.LongLength - offset);

            blocks[i] = new BlockProfile(offset, length, DataAnalyzer.Analyze(data.AsSpan((int)offset, length)));
        }

        return blocks;
    }

    private static double Throughput(int bytes, TimeSpan elapsed) =>
        elapsed.TotalSeconds <= 0 ? 0 : bytes / elapsed.TotalSeconds / (1024 * 1024);
}
