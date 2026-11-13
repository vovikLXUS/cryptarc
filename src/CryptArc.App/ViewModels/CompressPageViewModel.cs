using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CryptArc.App.Localization;
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

    private const string NoValue = "—";

    private static readonly Localizer L = Localizer.Instance;

    private byte[]? _data;

    // What the text properties were built from, kept so that a language switch can
    // build them again.
    private Func<string> _statusText = () => string.Empty;
    private RunSummary? _lastRun;

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
    private string _compressedSize = NoValue;

    /// <summary>The ratio of the last run as a caption, e.g. "42.0 % of input".</summary>
    [ObservableProperty]
    private string _ratioCaption = NoValue;

    [ObservableProperty]
    private string _elapsed = NoValue;

    [ObservableProperty]
    private string _throughput = NoValue;

    [ObservableProperty]
    private string _verification = NoValue;

    /// <summary>Creates the page with "Auto" selected.</summary>
    public CompressPageViewModel()
    {
        var choices = new List<CodecChoice> { new(null, null) };
        foreach (ICodec codec in CodecRegistry.All)
            choices.Add(new CodecChoice(codec.Name, codec.Id));

        CodecChoices = choices;
        _selectedCodec = choices[0];
        L.LanguageChanged += (_, _) => Retranslate();
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
        _lastRun = null;
        Tiles.Clear();
        Usage.Clear();
        Runs.Clear();
        BlocksDone = 0;
        BlockTotal = 0;
        HasResult = false;
        HasRun = false;
        ShowMap = false;
        MapTooLarge = false;
        SetStatus(() => L["Compress.Status.Ready"]);
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

        CodecChoice choice = SelectedCodec;
        var options = new BlockCompressionOptions
        {
            BlockSize = SelectedBlockSizeKiB * 1024,
            MaxDegreeOfParallelism = Threads,
            Codec = choice.Id,
            Verify = Verify,
        };

        BlockTotal = BlockCompressor.BlockCount(data.Length, options.BlockSize);
        BlocksDone = 0;
        HasResult = false;
        HasRun = true;
        PrepareTiles();

        int threads = Threads;
        SetStatus(() => threads == 1
            ? L["Compress.Status.OneThread"]
            : L.Format("Compress.Status.Threads", threads));

        // Created on the UI thread, so its callback runs there and may touch the tiles.
        var progress = new Progress<BlockResult>(OnBlockDone);

        try
        {
            var timer = Stopwatch.StartNew();
            BlockResult[] blocks = await Task.Run(() => threads == 1
                ? BlockCompressor.CompressSequential(data, options, progress, cancellationToken)
                : BlockCompressor.Compress(data, options, progress, cancellationToken), cancellationToken);
            timer.Stop();

            ShowResult(data.LongLength, blocks, timer.Elapsed, options, choice);
        }
        catch (OperationCanceledException)
        {
            int done = BlocksDone, total = BlockTotal;
            SetStatus(() => L.Format("Compress.Status.Cancelled", done, total));
        }
        catch (Exception exception)
        {
            SetStatus(() => L.Format("Common.Failed", exception.Message));
        }
    }

    private void SetStatus(Func<string> text)
    {
        _statusText = text;
        Status = text();
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

    private void ShowResult(long originalLength, BlockResult[] blocks, TimeSpan elapsed, BlockCompressionOptions options, CodecChoice choice)
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

        _lastRun = new RunSummary(compressed, ratio, elapsed, megabytesPerSecond, options.Verify ? allVerified : null);
        ShowLastRun();

        Usage.Clear();
        for (int id = 0; id < counts.Length; id++)
            if (counts[id] > 0)
                Usage.Add(new CodecUsage((CodecId)id, CodecName((CodecId)id), counts[id]));

        int blockSizeKiB = options.BlockSize / 1024;
        Runs.Insert(0, new RunRecord(
            choice,
            blockSizeKiB,
            options.MaxDegreeOfParallelism,
            elapsed,
            ratio,
            SpeedupAgainstBaseline(choice, blockSizeKiB, options.MaxDegreeOfParallelism, elapsed)));

        HasResult = true;
        SetStatus(() => L.Format("Compress.Status.Done", blocks.Length));
    }

    private void ShowLastRun()
    {
        if (_lastRun is not { } run)
            return;

        CompressedSize = L.Format("Unit.MiB", run.CompressedBytes / (1024.0 * 1024.0));
        RatioCaption = L.Format("Compress.Result.Ratio", L.Number(run.Ratio, "P1"));
        Elapsed = L.Format("Unit.Ms", run.Elapsed.TotalMilliseconds);
        Throughput = L.Format("Unit.MBps", run.MegabytesPerSecond);
        Verification = run.Verified switch
        {
            null => L["Compress.Verification.Off"],
            true => L["Compress.Verification.AllOk"],
            false => L["Compress.Verification.Failed"],
        };
    }

    // Speedup against the latest one-thread run with the same codec and block size:
    // null for the baseline itself or when there is none yet, NaN when the run was too
    // short to time. Wall-clock and indicative only; BlockCompressorBenchmarks is the
    // real measurement.
    private double? SpeedupAgainstBaseline(CodecChoice choice, int blockSizeKiB, int threads, TimeSpan elapsed)
    {
        if (threads == 1)
            return null;

        foreach (RunRecord run in Runs)
        {
            if (run.Threads == 1 && ReferenceEquals(run.Choice, choice) && run.BlockSizeKiB == blockSizeKiB)
                return elapsed.TotalSeconds <= 0 ? double.NaN : run.Elapsed.TotalSeconds / elapsed.TotalSeconds;
        }

        return null;
    }

    // Everything on the page that was built from translated text is built again.
    private void Retranslate()
    {
        Status = _statusText();
        ShowLastRun();
        CodecChoices[0].Retranslate();

        foreach (BlockTile tile in Tiles)
            tile.Retranslate();

        for (int i = 0; i < Runs.Count; i++)
            Runs[i] = Runs[i].Retranslated();
    }

    private static string CodecName(CodecId id) =>
        CodecRegistry.TryGet(id, out ICodec codec) ? codec.Name : id.ToString();

    private sealed record RunSummary(long CompressedBytes, double Ratio, TimeSpan Elapsed, double MegabytesPerSecond, bool? Verified);
}

/// <summary>An entry of the codec drop-down.</summary>
/// <param name="codecName">The codec's name, or null for the automatic choice, whose name is translated.</param>
/// <param name="id">The codec, or null for automatic choice per block.</param>
public sealed class CodecChoice(string? codecName, CodecId? id) : ObservableObject
{
    /// <summary>The codec, or null for automatic choice per block.</summary>
    public CodecId? Id { get; } = id;

    /// <summary>Shown in the list.</summary>
    public string Name => codecName ?? Localizer.Instance["Compress.Auto"];

    /// <summary>Name for narrow places such as the runs table: "Auto" instead of the full label.</summary>
    public string ShortName => codecName ?? Localizer.Instance["Compress.Auto.Short"];

    /// <summary>Tells the list that the names may have changed with the language.</summary>
    public void Retranslate()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ShortName));
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>How many blocks of a run ended up with one codec.</summary>
/// <param name="Codec">The codec.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Blocks">Number of blocks.</param>
public sealed record CodecUsage(CodecId Codec, string Name, int Blocks);

/// <summary>One completed run, for the history table.</summary>
/// <param name="Choice">Codec setting of the run.</param>
/// <param name="BlockSizeKiB">Block size in KiB.</param>
/// <param name="Threads">Worker threads.</param>
/// <param name="Elapsed">Wall-clock time of the run.</param>
/// <param name="Ratio">Compressed size as a fraction of the original.</param>
/// <param name="Speedup">
/// Against the matching one-thread run; null when there is nothing to compare with,
/// NaN when the run was too short to time.
/// </param>
/// <remarks>
/// The display texts are fixed when the row is created, in the language of that moment,
/// and are part of the record's equality. A data grid keeps the cells of a row it
/// considers unchanged, so after a language switch the rows are created again
/// (<see cref="Retranslated"/>) rather than asked for their texts a second time.
/// </remarks>
public sealed record RunRecord(CodecChoice Choice, int BlockSizeKiB, int Threads, TimeSpan Elapsed, double Ratio, double? Speedup)
{
    /// <summary>Codec setting for display: a codec name or "Auto".</summary>
    public string Codec { get; } = Choice.ShortName;

    /// <summary>Block size for display.</summary>
    public string BlockText { get; } = Localizer.Instance.Format("Compress.Runs.BlockSize", BlockSizeKiB);

    /// <summary>Elapsed time for display.</summary>
    public string ElapsedText { get; } = Localizer.Instance.Format("Unit.Ms", Elapsed.TotalMilliseconds);

    /// <summary>Ratio for display.</summary>
    public string RatioText { get; } = Localizer.Instance.Number(Ratio, "P1");

    /// <summary>Speedup for display, or why there is none.</summary>
    public string SpeedupText { get; } = Speedup switch
    {
        null when Threads == 1 => Localizer.Instance["Compress.Speedup.Baseline"],
        null => Localizer.Instance["Compress.Speedup.NeedsBaseline"],
        double.NaN => "—",
        { } speedup => Localizer.Instance.Number(speedup, "F2") + "×",
    };

    /// <summary>The same run with its texts in the current language.</summary>
    /// <returns>A new row.</returns>
    public RunRecord Retranslated() => new(Choice, BlockSizeKiB, Threads, Elapsed, Ratio, Speedup);
}

/// <summary>One square of the block map. Pending until its block finishes.</summary>
/// <param name="index">Block number.</param>
public partial class BlockTile(int index) : ObservableObject
{
    private BlockResult? _result;

    /// <summary>Block number.</summary>
    public int Index { get; } = index;

    /// <summary>The codec the block ended up with; null while pending.</summary>
    [ObservableProperty]
    private CodecId? _codec;

    /// <summary>False when verification ran and the block did not round-trip.</summary>
    [ObservableProperty]
    private bool _ok = true;

    [ObservableProperty]
    private string _toolTip = Localizer.Instance.Format("Compress.Tile.Pending", index);

    /// <summary>Marks the block done.</summary>
    /// <param name="block">Its result.</param>
    /// <param name="verified">Whether verification was on.</param>
    public void Complete(BlockResult block, bool verified)
    {
        _result = block;
        Codec = block.Codec;
        Ok = !verified || block.Verified;
        Retranslate();
    }

    /// <summary>Builds the tooltip again in the current language.</summary>
    public void Retranslate()
    {
        Localizer l = Localizer.Instance;
        if (_result is not { } block)
        {
            ToolTip = l.Format("Compress.Tile.Pending", Index);
            return;
        }

        double ratio = block.OriginalLength == 0 ? 1 : block.CompressedLength / (double)block.OriginalLength;
        ToolTip = l.Format("Compress.Tile.Done", block.Index, block.Offset, block.Codec, ratio) +
                  (Ok ? string.Empty : "\n" + l["Compress.Tile.Failed"]);
    }
}
