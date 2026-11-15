using CryptArc.Core.Analysis;

namespace CryptArc.Core.Sarc;

/// <summary>
/// Picks the <c>file_type</c> of a file from its first bytes and its statistics.
/// </summary>
/// <remarks>
/// <para>
/// This is a rule-based stand-in. Project 2 asks for a perceptron trained on the team's
/// <c>train/</c> set (zone "Аналіз файлу"); until that lands, these rules keep the field
/// meaningful. The value does not affect reading: it drives only the knapsack priorities
/// and the choice to skip compression for already-compressed data.
/// </para>
/// <para>Rules, first match wins:</para>
/// <list type="number">
/// <item>empty → synthetic;</item>
/// <item>a JPEG, PNG, GIF, ZIP, gzip, zlib, bzip2, xz, 7z or MP3 signature → already compressed;</item>
/// <item>an ELF, PE (<c>MZ</c>) or Mach-O signature → executable;</item>
/// <item>a BMP (<c>BM</c>) or PNM (<c>P1</c>…<c>P6</c>) signature → uncompressed raster;</item>
/// <item>entropy below 1 bit per byte, or above 7.5 with no signature → synthetic (runs, zeros, noise);</item>
/// <item>mostly printable text → source code, structured data or natural text by its punctuation;</item>
/// <item>anything else → executable.</item>
/// </list>
/// </remarks>
public static class SarcFileClassifier
{
    /// <summary>Classifies a file.</summary>
    /// <param name="data">The whole file, or a prefix of it.</param>
    /// <returns>The group.</returns>
    public static SarcFileType Classify(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return SarcFileType.Synthetic;

        if (IsCompressedFormat(data))
            return SarcFileType.AlreadyCompressed;

        if (StartsWith(data, [0x7F, (byte)'E', (byte)'L', (byte)'F']) || StartsWith(data, "MZ"u8)
            || StartsWith(data, [0xCF, 0xFA, 0xED, 0xFE]) || StartsWith(data, [0xCE, 0xFA, 0xED, 0xFE])
            || StartsWith(data, [0xCA, 0xFE, 0xBA, 0xBE]))
            return SarcFileType.Executable;

        if (StartsWith(data, "BM"u8) || data.Length >= 3 && data[0] == 'P' && data[1] is >= (byte)'1' and <= (byte)'6' && IsSpace(data[2]))
            return SarcFileType.UncompressedRaster;

        DataProfile profile = DataAnalyzer.Analyze(data);
        if (profile.Entropy < 1.0 || profile.Entropy > 7.5)
            return SarcFileType.Synthetic;

        if (profile.TextRatio >= 0.95)
            return ClassifyText(data[..Math.Min(data.Length, DataAnalyzer.SampleSize)]);

        return SarcFileType.Executable;
    }

    private static SarcFileType ClassifyText(ReadOnlySpan<byte> text)
    {
        int lines = 1, braces = 0, semicolons = 0, separators = 0, letters = 0, spaces = 0;
        foreach (byte b in text)
        {
            switch (b)
            {
                case (byte)'\n': lines++; break;
                case (byte)'{' or (byte)'}' or (byte)'(' or (byte)')' or (byte)'[' or (byte)']' or (byte)'=': braces++; break;
                case (byte)';' or (byte)':': semicolons++; break;
                case (byte)',' or (byte)'\t' or (byte)'|': separators++; break;
                case (byte)' ': spaces++; break;
                default:
                    if (b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z')
                        letters++;
                    break;
            }
        }

        double perLine(int count) => count / (double)lines;

        if (perLine(separators) >= 2 && separators > spaces / 2)
            return SarcFileType.StructuredData;

        if (perLine(braces + semicolons) >= 1.5)
            return SarcFileType.SourceCode;

        return letters > 0 || spaces > 0 ? SarcFileType.NaturalText : SarcFileType.StructuredData;
    }

    private static bool IsCompressedFormat(ReadOnlySpan<byte> data) =>
        StartsWith(data, [0xFF, 0xD8, 0xFF])                       // JPEG
        || StartsWith(data, [0x89, (byte)'P', (byte)'N', (byte)'G']) // PNG
        || StartsWith(data, "GIF8"u8)
        || StartsWith(data, [(byte)'P', (byte)'K', 0x03, 0x04])    // ZIP
        || StartsWith(data, [0x1F, 0x8B])                          // gzip
        || StartsWith(data, [0x1F, 0x9D])                          // compress (.Z)
        || data.Length >= 2 && data[0] == 0x78 && (data[0] << 8 | data[1]) % 31 == 0 // zlib
        || StartsWith(data, "BZh"u8)
        || StartsWith(data, [0xFD, (byte)'7', (byte)'z', (byte)'X', (byte)'Z'])
        || StartsWith(data, [(byte)'7', (byte)'z', 0xBC, 0xAF])
        || StartsWith(data, "ID3"u8)
        || data.Length >= 2 && data[0] == 0xFF && (data[1] & 0xE0) == 0xE0; // MPEG audio frame

    private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix) => data.StartsWith(prefix);

    private static bool IsSpace(byte value) => value is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t';
}
