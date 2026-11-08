using System;
using System.Collections.Generic;
using System.Text;
using CryptArc.Core.Analysis;
using CryptArc.Core.Coding;
using CryptArc.Core.Transforms;

namespace CryptArc.App.Services;

/// <summary>
/// Runs a short input through the stages of <c>BwtCodec</c> one at a time —
/// BWT, move-to-front, zero-run coding, Huffman — and keeps every intermediate
/// result for display. All the work is done by Core; this only collects it.
/// </summary>
public static class TransformLab
{
    /// <summary>Longest input the lab accepts, in bytes. It is a teaching view, not a codec.</summary>
    public const int MaxInputBytes = 4096;

    /// <summary>The sorted-rotations table is drawn in full up to this many rows.</summary>
    public const int MaxRotationRows = 40;

    /// <summary>Runs every stage on <paramref name="input"/>.</summary>
    /// <param name="input">At most <see cref="MaxInputBytes"/> bytes.</param>
    /// <returns>Every stage's output and statistics.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The input is longer than <see cref="MaxInputBytes"/>.</exception>
    public static LabReport Run(byte[] input)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(input.Length, MaxInputBytes, nameof(input));

        // 1. Burrows–Wheeler.
        byte[] bwt = new byte[input.Length];
        int primaryIndex = BurrowsWheeler.Forward(input, bwt);

        // 2. Move-to-front, in place on a copy.
        byte[] mtf = (byte[])bwt.Clone();
        MoveToFront.Encode(mtf);

        // 3. Zero runs to RUNA/RUNB digits.
        byte[] rle = new byte[ZeroRunLength.MaxEncodedSize(mtf.Length)];
        int rleLength = ZeroRunLength.Encode(mtf, rle);
        Array.Resize(ref rle, rleLength);

        // 4. Huffman over the final stream, and over the raw input for comparison.
        HuffmanTable afterPipeline = Huffman(rle);
        HuffmanTable rawOnly = Huffman(input);

        return new LabReport(
            Input: Stage(input),
            Rotations: SortedRotations(input),
            PrimaryIndex: primaryIndex,
            Bwt: Stage(bwt),
            Mtf: Stage(mtf),
            Rle: Stage(rle),
            Huffman: afterPipeline,
            HuffmanOnRawInput: rawOnly);
    }

    private static StageResult Stage(byte[] bytes)
    {
        int zeros = 0;
        foreach (byte value in bytes)
            if (value == 0)
                zeros++;

        double entropy = bytes.Length == 0 ? 0 : DataAnalyzer.Analyze(bytes).Entropy;
        return new StageResult(bytes, entropy, bytes.Length == 0 ? 0 : zeros / (double)bytes.Length);
    }

    // The matrix BurrowsWheeler never builds: every rotation of input + '$', sorted.
    // SuffixArray gives the order; row 0 is always the rotation that starts with '$'.
    private static RotationRow[] SortedRotations(byte[] input)
    {
        int n = input.Length;
        if (n == 0)
            return [];

        int[] suffixArray = new int[n];
        SuffixArray.Build(input, suffixArray);

        int rows = Math.Min(n + 1, MaxRotationRows);
        var result = new RotationRow[rows];
        result[0] = new RotationRow("$" + Printable(input), Printable(input[n - 1]), IsSentinelRow: false);

        for (int row = 1; row < rows; row++)
        {
            int start = suffixArray[row - 1];
            var text = new StringBuilder(n + 1);
            text.Append(Printable(input.AsSpan(start)));
            text.Append('$');
            text.Append(Printable(input.AsSpan(0, start)));

            string last = start == 0 ? "$" : Printable(input[start - 1]);
            result[row] = new RotationRow(text.ToString(), last, IsSentinelRow: start == 0);
        }

        return result;
    }

    private static HuffmanTable Huffman(byte[] data)
    {
        var frequencies = new int[HuffmanCodeBook.AlphabetSize];
        var lengths = new byte[HuffmanCodeBook.AlphabetSize];
        var codes = new uint[HuffmanCodeBook.AlphabetSize];
        long bits = HuffmanCodeBook.Build(data, frequencies, lengths, codes);

        var rows = new List<HuffmanRow>();
        for (int symbol = 0; symbol < HuffmanCodeBook.AlphabetSize; symbol++)
        {
            if (lengths[symbol] == 0)
                continue;

            string code = Convert.ToString(codes[symbol], 2).PadLeft(lengths[symbol], '0');
            rows.Add(new HuffmanRow(symbol, frequencies[symbol], lengths[symbol], code));
        }

        // Most frequent first, the way the code assigns lengths.
        rows.Sort((a, b) => b.Count != a.Count ? b.Count.CompareTo(a.Count) : a.Symbol.CompareTo(b.Symbol));
        return new HuffmanTable(rows, bits);
    }

    /// <summary>Renders bytes as text, with a middle dot for anything unprintable.</summary>
    /// <param name="bytes">The bytes.</param>
    public static string Printable(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder(bytes.Length);
        foreach (byte value in bytes)
            text.Append(Printable(value));
        return text.ToString();
    }

    /// <summary>One byte as a visible character: itself if printable ASCII, '␣' for space, '·' otherwise.</summary>
    /// <param name="value">The byte.</param>
    public static string Printable(byte value) => value switch
    {
        (byte)' ' => "␣",
        >= 0x21 and <= 0x7E => ((char)value).ToString(),
        _ => "·",
    };
}

/// <summary>Everything the lab shows for one input.</summary>
/// <param name="Input">The input itself.</param>
/// <param name="Rotations">The first rows of the sorted rotation matrix.</param>
/// <param name="PrimaryIndex">The row of the original text, as BurrowsWheeler returns it.</param>
/// <param name="Bwt">The last column: the BWT output.</param>
/// <param name="Mtf">After move-to-front.</param>
/// <param name="Rle">After zero-run coding.</param>
/// <param name="Huffman">The Huffman code for the final stream.</param>
/// <param name="HuffmanOnRawInput">The Huffman code for the input with no transforms, for comparison.</param>
public sealed record LabReport(
    StageResult Input,
    RotationRow[] Rotations,
    int PrimaryIndex,
    StageResult Bwt,
    StageResult Mtf,
    StageResult Rle,
    HuffmanTable Huffman,
    HuffmanTable HuffmanOnRawInput);

/// <summary>One stage's output.</summary>
/// <param name="Bytes">The bytes.</param>
/// <param name="Entropy">Order-0 entropy, bits per byte.</param>
/// <param name="ZeroShare">Fraction of bytes that are zero.</param>
public sealed record StageResult(byte[] Bytes, double Entropy, double ZeroShare);

/// <summary>A row of the sorted rotation matrix.</summary>
/// <param name="Text">The rotation, '$' marking the end of the input.</param>
/// <param name="Last">Its last character: the BWT output for this row.</param>
/// <param name="IsSentinelRow">True for the row whose last character is '$': the original text.</param>
public sealed record RotationRow(string Text, string Last, bool IsSentinelRow)
{
    /// <summary>The rotation without its last character, which is shown separately.</summary>
    public string Body => Text.Length > 0 ? Text[..^1] : string.Empty;
}

/// <summary>A Huffman code for one buffer.</summary>
/// <param name="Rows">One row per symbol that occurs, most frequent first.</param>
/// <param name="Bits">Coded size in bits, without the table header.</param>
public sealed record HuffmanTable(IReadOnlyList<HuffmanRow> Rows, long Bits);

/// <summary>One symbol of a Huffman code.</summary>
/// <param name="Symbol">Byte value.</param>
/// <param name="Count">How often it occurs.</param>
/// <param name="Length">Code length in bits.</param>
/// <param name="Code">The code, most significant bit first.</param>
public sealed record HuffmanRow(int Symbol, int Count, int Length, string Code);
