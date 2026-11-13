using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CryptArc.Core;
using CryptArc.Core.Blocks;

namespace CryptArc.App.ViewModels;

/// <summary>
/// The "Compress" page: compress the loaded file block by block with
/// <see cref="BlockCompressor"/>, watch the blocks finish, and compare runs.
/// </summary>
/// <remarks>
/// Nothing is written to disk: the page shows what compressing would cost and save.
/// <see cref="ArchivePageViewModel"/> is the page that produces an archive.
/// </remarks>
public partial class CompressPageViewModel : ObservableObject
{
    /// <summary>
    /// Above this many blocks the map is not drawn; thousands of tiles would cost
    /// more to lay out than they show.
    /// </summary>
    public const int MaxMapTiles = 2048;

    private byte[]? _data;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private bool _hasFile;

    [ObservableProperty]
    private CodecChoice _selectedCodec;

    [ObservableProperty]
    private int _selectedBlockSizeKiB = BlockCompressor.DefaultBlockSize / 1024;

    [ObservableProperty]
    private int _threads = Environment.ProcessorCount;

    [ObservableProperty]
    private bool _verify = true;

    [ObservableProperty]
    private int _blocksDone;

    [ObservableProperty]
    private int _blockTotal;

    [ObservableProperty]
    private bool _showMap;

    /// <summary>The last run had more blocks than <see cref="MaxMapTiles"/>, so no map is drawn.</summary>
    [ObservableProperty]
    private bool _mapTooLarge;

    /// <summary>A run has started at least once since the file was loaded.</summary>
    [ObservableProperty]
    private bool _hasRun;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private string _compressedSize = "—";

    [ObservableProperty]
    private string _ratio = "—";

    [ObservableProperty]
    private string _elapsed = "—";

    [ObservableProperty]
    private string _throughput = "—";

    [ObservableProperty]
    private string _verification = "—";

    /// <summary>Creates the page with "Auto" selected.</summary>
    public CompressPageViewModel()
    {
        var choices = new List<CodecChoice> { new("Auto (per block)", null) };
        foreach (ICodec codec in CodecRegistry.All)
            choices.Add(new CodecChoice(codec.Name, codec.Id));

        CodecChoices = choices;
        _selectedCodec = choices[0];
    }

    /// <summary>"Auto" followed by every registered codec.</summary>
    public IReadOnlyList<CodecChoice> CodecChoices { get; }

    /// <summary>Block sizes offered, in KiB.</summary>
    public IReadOnlyList<int> BlockSizesKiB { get; } = [64, 128, 256, 512, 1024, 4096];

    /// <summary>Upper bound of the thread slider: the number of logical processors.</summary>
    public int MaxThreads { get; } = Environment.ProcessorCount;

    /// <summary>One tile per block of the current run, updated as blocks finish.</summary>
    public ObservableCollection<BlockTile> Tiles { get; } = [];

    /// <summary>How many blocks ended up with each codec in the last run.</summary>
    public ObservableCollection<CodecUsage> Usage { get; } = [];

    /// <summary>Every completed run since the file was loaded, newest first.</summary>
    public ObservableCollection<RunRecord> Runs { get; } = [];

    /// <summary>Hands the page a newly loaded file and clears the previous one's runs.</summary>
    /// <param name="data">The whole file contents.</param>
    public void SetFile(byte[] data)
    {
        if (RunCommand.IsRunning)
            RunCommand.Cancel();

        _data = data;
        Tiles.Clear();
        Usage.Clear();
        Runs.Clear();
        BlocksDone = 0;
        BlockTotal = 0;
        HasResult = false;
        HasRun = false;
        ShowMap = false;
        MapTooLarge = false;
        Status = "Pick the settings and press Compress.";
        HasFile = true;
    }

    private bool CanRun() => HasFile;

    /// <summary>Compresses the loaded file with the current settings.</summary>
    /// <param name="cancellationToken">Set by the Cancel button.</param>
    [RelayCommand(CanExecute = nameof(CanRun), IncludeCancelCommand = true)]
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_data is not { } data)
            return;

        var options = new BlockCompressionOptions
        {
            BlockSize = SelectedBlockSizeKiB * 1024,
            MaxDegreeOfParallelism = Threads,
            Codec = SelectedCodec.Id,
            Verify = Verify,
        };

        BlockTotal = BlockCompressor.BlockCount(data.Length, options.BlockSize);
        BlocksDone = 0;
        HasResult = false;
        HasRun = true;
        PrepareTiles();
        Status = Threads == 1 ? "Compressing on one thread (sequential baseline)…" : $"Compressing on {Threads} threads…";

        // Created on the UI thread, so its callback runs there and may touch the tiles.
        var progress = new Progress<BlockResult>(OnBlockDone);
        int threads = Threads;

        try
        {
            var timer = Stopwatch.StartNew();
            BlockResult[] blocks = await Task.Run(() => threads == 1
                ? BlockCompressor.CompressSequential(data, options, progress, cancellationToken)
                : BlockCompressor.Compress(data, options, progress, cancellationToken), cancellationToken);
            timer.Stop();

            ShowResult(data.LongLength, blocks, timer.Elapsed, options);
        }
        catch (OperationCanceledException)
        {
            Status = $"Cancelled after {BlocksDone} of {BlockTotal} blocks.";
        }
        catch (Exception exception)
        {
            Status = $"Failed: {exception.Message}";
        }
    }

    private void PrepareTiles()
    {
        Tiles.Clear();
        ShowMap = BlockTotal <= MaxMapTiles;
        MapTooLarge = !ShowMap;
        if (!ShowMap)
            return;

        for (int i = 0; i < BlockTotal; i++)
            Tiles.Add(new BlockTile(i));
    }

    private void OnBlockDone(BlockResult block)
    {
        BlocksDone++;
        if (ShowMap && block.Index < Tiles.Count)
            Tiles[block.Index].Complete(block, Verify);
    }

    private void ShowResult(long originalLength, BlockResult[] blocks, TimeSpan elapsed, BlockCompressionOptions options)
    {
        long compressed = 0;
        bool allVerified = true;
        var counts = new int[256];

        foreach (BlockResult block in blocks)
        {
            compressed += block.CompressedLength;
            allVerified &= block.Verified;
            counts[(byte)block.Codec]++;
        }

        double ratio = originalLength == 0 ? 1 : compressed / (double)originalLength;
        double megabytesPerSecond = elapsed.TotalSeconds <= 0 ? 0 : originalLength / elapsed.TotalSeconds / (1024 * 1024);

        CompressedSize = $"{compressed / (1024.0 * 1024.0):N1} MiB";
        Ratio = $"{ratio:P1}";
        Elapsed = $"{elapsed.TotalMilliseconds:N0} ms";
        Throughput = $"{megabytesPerSecond:N0} MB/s";
        Verification = !options.Verify ? "Off" : allVerified ? "All OK" : "FAILED";

        Usage.Clear();
        for (int id = 0; id < counts.Length; id++)
            if (counts[id] > 0)
                Usage.Add(new CodecUsage((CodecId)id, CodecName((CodecId)id), counts[id]));

        Runs.Insert(0, new RunRecord(
            SelectedCodec.ShortName,
            options.BlockSize / 1024,
            options.MaxDegreeOfParallelism,
            elapsed,
            ratio,
            SpeedupAgainstBaseline(options, elapsed)));

        HasResult = true;
        Status = $"Done: {blocks.Length} blocks.";
    }

    // Speedup against the latest one-thread run with the same codec and block size.
    // Wall-clock and indicative only; BlockCompressorBenchmarks is the real measurement.
    private string SpeedupAgainstBaseline(BlockCompressionOptions options, TimeSpan elapsed)
    {
        if (options.MaxDegreeOfParallelism == 1)
            return "baseline";

        foreach (RunRecord run in Runs)
        {
            if (run.Threads == 1 && run.Codec == SelectedCodec.ShortName && run.BlockSizeKiB == options.BlockSize / 1024)
                return elapsed.TotalSeconds <= 0 ? "—" : $"{run.Elapsed.TotalSeconds / elapsed.TotalSeconds:F2}×";
        }

        return "needs 1-thread run";
    }

    private static string CodecName(CodecId id) =>
        CodecRegistry.TryGet(id, out ICodec codec) ? codec.Name : id.ToString();
}

/// <summary>An entry of the codec drop-down.</summary>
/// <param name="Name">Shown in the list.</param>
/// <param name="Id">The codec, or null for automatic choice per block.</param>
public sealed record CodecChoice(string Name, CodecId? Id)
{
    /// <summary>Name for narrow places such as the runs table: "Auto" instead of the full label.</summary>
    public string ShortName => Id is null ? "Auto" : Name;

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>How many blocks of a run ended up with one codec.</summary>
/// <param name="Codec">The codec.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Blocks">Number of blocks.</param>
public sealed record CodecUsage(CodecId Codec, string Name, int Blocks);

/// <summary>One completed run, for the history table.</summary>
/// <param name="Codec">Codec setting: a codec name or "Auto".</param>
/// <param name="BlockSizeKiB">Block size in KiB.</param>
/// <param name="Threads">Worker threads.</param>
/// <param name="Elapsed">Wall-clock time of the run.</param>
/// <param name="Ratio">Compressed size as a fraction of the original.</param>
/// <param name="Speedup">Against the matching one-thread run, as display text.</param>
public sealed record RunRecord(string Codec, int BlockSizeKiB, int Threads, TimeSpan Elapsed, double Ratio, string Speedup)
{
    /// <summary>Elapsed time for display.</summary>
    public string ElapsedText => $"{Elapsed.TotalMilliseconds:N0} ms";

    /// <summary>Ratio for display.</summary>
    public string RatioText => $"{Ratio:P1}";
}

/// <summary>One square of the block map. Pending until its block finishes.</summary>
/// <param name="index">Block number.</param>
public partial class BlockTile(int index) : ObservableObject
{
    /// <summary>Block number.</summary>
    public int Index { get; } = index;

    /// <summary>The codec the block ended up with; null while pending.</summary>
    [ObservableProperty]
    private CodecId? _codec;

    /// <summary>False when verification ran and the block did not round-trip.</summary>
    [ObservableProperty]
    private bool _ok = true;

    [ObservableProperty]
    private string _toolTip = $"Block {index}: pending";

    /// <summary>Marks the block done.</summary>
    /// <param name="block">Its result.</param>
    /// <param name="verified">Whether verification was on.</param>
    public void Complete(BlockResult block, bool verified)
    {
        Codec = block.Codec;
        Ok = !verified || block.Verified;
        double ratio = block.OriginalLength == 0 ? 1 : block.CompressedLength / (double)block.OriginalLength;
        ToolTip = $"Block {block.Index} · offset {block.Offset:N0}\n{block.Codec} · {ratio:P1}" +
                  (Ok ? string.Empty : "\nround trip FAILED");
    }
}
