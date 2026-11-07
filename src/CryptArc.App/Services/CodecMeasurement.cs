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
    /// <inheritdoc />
    public MeasurementReport Measure(byte[] data)
    {
        DataProfile profile = DataAnalyzer.Analyze(data);
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

        return new MeasurementReport(profile, crc, results);
    }

    private static double Throughput(int bytes, TimeSpan elapsed) =>
        elapsed.TotalSeconds <= 0 ? 0 : bytes / elapsed.TotalSeconds / (1024 * 1024);
}
