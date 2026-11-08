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
/// <param name="Blocks">
/// Per-block profiles for the entropy heatmap, at most
/// <see cref="CodecMeasurement.MaxHeatmapBlocks"/> of them, in file order.
/// </param>
/// <param name="Histogram">How often each byte value occurs in the whole file; 256 entries.</param>
public sealed record MeasurementReport(
    DataProfile Profile,
    uint Crc,
    CodecResult[] Results,
    BlockProfile[] Blocks,
    long[] Histogram);

/// <summary>The analyzer's profile of one block of the file.</summary>
/// <param name="Offset">Byte offset of the block in the file.</param>
/// <param name="Length">Length of the block in bytes.</param>
/// <param name="Profile">Entropy, repeat and text ratios of that block.</param>
public readonly record struct BlockProfile(long Offset, int Length, DataProfile Profile);
