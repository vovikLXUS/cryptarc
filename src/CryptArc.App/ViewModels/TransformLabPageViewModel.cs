using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CryptArc.App.Services;

namespace CryptArc.App.ViewModels;

/// <summary>
/// The "Transform lab" page: a short text run through BWT, move-to-front,
/// zero-run coding and Huffman, every stage shown. Works without a loaded file.
/// </summary>
public partial class TransformLabPageViewModel : ObservableObject
{
    private const int PreviewValues = 400;

    [ObservableProperty]
    private string _input = "banana_bandana_banana_bandana";

    [ObservableProperty]
    private string _inputNote = string.Empty;

    [ObservableProperty]
    private string _rotationsCaption = string.Empty;

    [ObservableProperty]
    private string _bwtPreview = string.Empty;

    [ObservableProperty]
    private string _mtfPreview = string.Empty;

    [ObservableProperty]
    private string _rlePreview = string.Empty;

    [ObservableProperty]
    private string _huffmanRawSize = string.Empty;

    [ObservableProperty]
    private string _huffmanPipelineSize = string.Empty;

    [ObservableProperty]
    private double _huffmanRawShare;

    [ObservableProperty]
    private double _huffmanPipelineShare;

    [ObservableProperty]
    private string _verdict = string.Empty;

    /// <summary>Creates the page and runs the default example.</summary>
    public TransformLabPageViewModel() => Recompute();

    /// <summary>Example inputs offered as buttons.</summary>
    public IReadOnlyList<LabPreset> Presets { get; } =
    [
        new("banana", "banana_bandana_banana_bandana"),
        new("abracadabra", "abracadabra abracadabra abracadabra"),
        new("English", "the cat sat on the mat and the dog sat on the log, so the cat and the dog sat."),
        new("Source code", "for (int i = 0; i < n; i++) { sum += a[i]; }\nfor (int j = 0; j < n; j++) { sum += b[j]; }\n"),
        new("Random", "q7Zp2Lx9Vb4Nk1Rt8Mw3Yc6Hs0Jd5Gf"),
    ];

    /// <summary>One chip per stage for the summary row.</summary>
    public ObservableCollection<StageChip> Stages { get; } = [];

    /// <summary>The first rows of the sorted rotation matrix.</summary>
    public ObservableCollection<RotationRow> Rotations { get; } = [];

    /// <summary>The Huffman code of the final stream, most frequent symbol first.</summary>
    public ObservableCollection<HuffmanRowView> HuffmanCodes { get; } = [];

    [RelayCommand]
    private void UsePreset(LabPreset preset) => Input = preset.Text;

    partial void OnInputChanged(string value) => Recompute();

    private void Recompute()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Input);
        InputNote = bytes.Length > TransformLab.MaxInputBytes
            ? $"Only the first {TransformLab.MaxInputBytes:N0} bytes are used."
            : $"{bytes.Length:N0} bytes";
        if (bytes.Length > TransformLab.MaxInputBytes)
            Array.Resize(ref bytes, TransformLab.MaxInputBytes);

        LabReport report = TransformLab.Run(bytes);

        Stages.Clear();
        Stages.Add(Chip("Input", report.Input, isLast: false));
        Stages.Add(Chip("BWT", report.Bwt, isLast: false));
        Stages.Add(Chip("Move-to-front", report.Mtf, isLast: false));
        Stages.Add(Chip("Zero runs", report.Rle, isLast: true));

        Rotations.Clear();
        foreach (RotationRow row in report.Rotations)
            Rotations.Add(row);
        RotationsCaption = report.Rotations.Length < bytes.Length + 1
            ? $"First {report.Rotations.Length} of {bytes.Length + 1} rows. Original text is row {report.PrimaryIndex}."
            : $"All {report.Rotations.Length} rows. Original text is row {report.PrimaryIndex}, marked.";

        BwtPreview = TransformLab.Printable(report.Bwt.Bytes);
        MtfPreview = Numbers(report.Mtf.Bytes);
        RlePreview = Numbers(report.Rle.Bytes);

        long rawBytes = (report.HuffmanOnRawInput.Bits + 7) / 8;
        long pipelineBytes = (report.Huffman.Bits + 7) / 8;
        double inputLength = Math.Max(1, bytes.Length);
        HuffmanRawShare = Math.Min(1, rawBytes / inputLength);
        HuffmanPipelineShare = Math.Min(1, pipelineBytes / inputLength);
        HuffmanRawSize = $"{rawBytes:N0} B · {rawBytes / inputLength:P0}";
        HuffmanPipelineSize = $"{pipelineBytes:N0} B · {pipelineBytes / inputLength:P0}";
        Verdict = bytes.Length == 0
            ? string.Empty
            : pipelineBytes < rawBytes
                ? $"The transforms save {rawBytes - pipelineBytes:N0} byte{(rawBytes - pipelineBytes == 1 ? "" : "s")} over Huffman alone. Table headers are not counted."
                : "On this input the transforms do not help: there is too little repetition for BWT to group. Table headers are not counted.";

        HuffmanCodes.Clear();
        foreach (HuffmanRow row in report.Huffman.Rows)
            HuffmanCodes.Add(new HuffmanRowView(row.Symbol, row.Count, row.Length, row.Code));
    }

    private static StageChip Chip(string name, StageResult stage, bool isLast) =>
        new(name, $"{stage.Bytes.Length:N0} B", $"{stage.Entropy:F2} bits/byte", $"{stage.ZeroShare:P0} zeros", !isLast);

    private static string Numbers(byte[] bytes)
    {
        int count = Math.Min(bytes.Length, PreviewValues);
        var text = new StringBuilder(count * 3);
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
                text.Append(' ');
            text.Append(bytes[i]);
        }

        if (bytes.Length > count)
            text.Append($" … (+{bytes.Length - count:N0})");
        return text.ToString();
    }
}

/// <summary>An example input.</summary>
/// <param name="Name">Button label.</param>
/// <param name="Text">The text it loads.</param>
public sealed record LabPreset(string Name, string Text);

/// <summary>One stage in the summary row.</summary>
/// <param name="Name">Stage name.</param>
/// <param name="Size">Output size.</param>
/// <param name="Entropy">Order-0 entropy of the output.</param>
/// <param name="Zeros">Share of zero bytes in the output.</param>
/// <param name="HasArrow">Whether an arrow to the next stage follows.</param>
public sealed record StageChip(string Name, string Size, string Entropy, string Zeros, bool HasArrow);

/// <summary>One row of the Huffman code table.</summary>
/// <param name="Symbol">Symbol value of the final stream (an MTF rank or a RUNA/RUNB digit).</param>
/// <param name="Count">Occurrences.</param>
/// <param name="Length">Code length in bits.</param>
/// <param name="Code">The code, most significant bit first.</param>
public sealed record HuffmanRowView(int Symbol, int Count, int Length, string Code)
{
    /// <summary>
    /// What the symbol stands for after zero-run coding: the two run digits, or a
    /// move-to-front rank shifted up by one.
    /// </summary>
    public string Meaning => Symbol switch
    {
        0 => "RUNA (zero-run digit)",
        1 => "RUNB (zero-run digit)",
        255 => "escape: rank 254 or 255 follows",
        _ => $"MTF rank {Symbol - 1}",
    };
}
