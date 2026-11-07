using CryptArc.App.ViewModels;
using CryptArc.Core.Analysis;

namespace CryptArc.App.Services;

/// <summary>
/// Profiles a buffer and round-trips it through every registered codec, timing each one.
/// </summary>
/// <remarks>
/// Synchronous and CPU-bound: the caller decides which thread it runs on. The view model
/// wraps it in <c>Task.Run</c>, a test can call it directly.
/// </remarks>
public interface ICodecMeasurement
{
    /// <summary>Analyses <paramref name="data"/> and measures every codec in the registry.</summary>
    /// <param name="data">The whole file contents.</param>
    /// <returns>The statistical profile, the CRC-32 of the input and one row per codec.</returns>
    MeasurementReport Measure(byte[] data);
}

/// <summary>Everything the main window shows about one file.</summary>
/// <param name="Profile">Entropy, repeat and text ratios from the analyzer.</param>
/// <param name="Crc">CRC-32 of the original data.</param>
/// <param name="Results">One row per registered codec, in registry order.</param>
public sealed record MeasurementReport(DataProfile Profile, uint Crc, CodecResult[] Results);
