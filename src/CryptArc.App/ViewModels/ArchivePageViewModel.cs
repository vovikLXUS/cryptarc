using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CryptArc.App.Localization;
using CryptArc.Core;
using CryptArc.Core.Container;

namespace CryptArc.App.ViewModels;

/// <summary>
/// The "Encrypted archive" page: pack files and folders into a <c>.cryptarc</c>
/// archive, and open one to list, verify and restore it.
/// </summary>
/// <remarks>
/// A password is only ever a method argument here. It is never stored: an opened
/// archive keeps its <see cref="ArchiveReader"/>, which holds the derived keys, so
/// verifying and extracting do not ask for the password again.
/// </remarks>
public sealed partial class ArchivePageViewModel : ObservableObject, IDisposable
{
    private static readonly Localizer L = Localizer.Instance;

    private ArchiveReader? _reader;
    private CancellationTokenSource? _cancellation;

    // What Status was built from, kept so that a language switch can build it again.
    private Func<string> _statusText = () => L["Archive.Status.Initial"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanCreate), nameof(CanUnlock), nameof(CanUseArchive))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = L["Archive.Status.Initial"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    private bool _hasSources;

    [ObservableProperty]
    private CodecChoice _selectedCodec;

    [ObservableProperty]
    private int _threads = Environment.ProcessorCount;

    /// <summary>Full path of the chosen archive, or null when none is chosen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUnlock), nameof(HasArchive))]
    private string? _archivePath;

    [ObservableProperty]
    private string _archiveName = L["Archive.NoArchive"];

    /// <summary>The chosen archive has been unlocked and its file table is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUnlock), nameof(CanUseArchive), nameof(IsLocked))]
    private bool _isOpen;

    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>Creates the page with "Auto" selected.</summary>
    public ArchivePageViewModel()
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

    /// <summary>Upper bound of the thread slider: the number of logical processors.</summary>
    public int MaxThreads { get; } = Environment.ProcessorCount;

    /// <summary>Files and folders to pack, as full paths.</summary>
    public ObservableCollection<string> Sources { get; } = [];

    /// <summary>The files of the opened archive, in archive order.</summary>
    public ObservableCollection<ArchiveEntryRow> Entries { get; } = [];

    /// <summary>No operation is running.</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>There is something to pack and no operation is running.</summary>
    public bool CanCreate => HasSources && !IsBusy;

    /// <summary>An archive is chosen.</summary>
    public bool HasArchive => ArchivePath is not null;

    /// <summary>The archive is chosen but not unlocked yet.</summary>
    public bool IsLocked => !IsOpen;

    /// <summary>An archive is chosen, still locked, and no operation is running.</summary>
    public bool CanUnlock => ArchivePath is not null && !IsOpen && !IsBusy;

    /// <summary>An archive is unlocked and no operation is running.</summary>
    public bool CanUseArchive => IsOpen && !IsBusy;

    /// <summary>Adds files and folders to the list to pack; paths already listed are skipped.</summary>
    /// <param name="paths">Full paths of files or folders.</param>
    public void AddSources(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (!Sources.Contains(path))
                Sources.Add(path);
        }

        HasSources = Sources.Count > 0;
    }

    /// <summary>Checks a new password before the save dialog is shown.</summary>
    /// <param name="password">The password.</param>
    /// <param name="repeat">The password typed a second time.</param>
    /// <returns>True when the password can be used; otherwise the reason is in <see cref="Status"/>.</returns>
    public bool ValidateNewPassword(string? password, string? repeat)
    {
        if (string.IsNullOrEmpty(password))
        {
            SetStatus(() => L["Archive.Status.NeedNewPassword"]);
            return false;
        }

        if (!string.Equals(password, repeat, StringComparison.Ordinal))
        {
            SetStatus(() => L["Archive.Status.Mismatch"]);
            return false;
        }

        return true;
    }

    /// <summary>Packs the listed files into a new archive. An existing file is never replaced.</summary>
    /// <param name="archivePath">Where to create the archive.</param>
    /// <param name="password">The password; not kept after the call.</param>
    public async Task CreateAsync(string archivePath, string password)
    {
        if (IsBusy || Sources.Count == 0)
            return;

        string[] paths = [.. Sources];
        var options = new ArchiveOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Threads),
            Codec = SelectedCodec.Id,
        };

        (bool ok, (int Files, long Size, TimeSpan Elapsed) result) = await RunAsync("Archive.Status.Packing", token =>
        {
            var sources = new List<ArchiveSource>();
            foreach (string path in paths)
                sources.AddRange(ArchiveSource.FromPath(path));

            var timer = Stopwatch.StartNew();
            ArchiveWriter.CreateFile(archivePath, sources, password, options, token);
            timer.Stop();

            return (sources.Count, new FileInfo(archivePath).Length, timer.Elapsed);
        });

        if (ok)
        {
            SetStatus(() => L.Format("Archive.Status.Created", Path.GetFileName(archivePath), result.Files,
                FormatSize(result.Size), result.Elapsed.TotalSeconds));
        }
    }

    /// <summary>Chooses the archive to open; it stays locked until <see cref="UnlockAsync"/>.</summary>
    /// <param name="path">Full path of the archive.</param>
    public void SelectArchive(string path)
    {
        if (IsBusy)
            return;

        CloseArchive();
        ArchivePath = path;
        ArchiveName = Path.GetFileName(path);
        SetStatus(() => L["Archive.Status.EnterAndUnlock"]);
    }

    /// <summary>
    /// Opens the chosen archive: derives the keys, checks the HMAC over the whole file
    /// and reads the file table.
    /// </summary>
    /// <param name="password">The password; not kept after the call.</param>
    public async Task UnlockAsync(string? password)
    {
        if (!CanUnlock || ArchivePath is not { } path)
            return;

        if (string.IsNullOrEmpty(password))
        {
            SetStatus(() => L["Archive.Status.EnterPassword"]);
            return;
        }

        (bool ok, ArchiveReader reader) = await RunAsync(
            "Archive.Status.Unlocking",
            token => ArchiveReader.OpenFile(path, password, token));

        if (!ok)
            return;

        _reader = reader;
        ShowEntries(reader);
        IsOpen = true;
        SetStatus(() => L["Archive.Status.Unlocked"]);
    }

    // The file table and the summary line of the opened archive, in the current language.
    private void ShowEntries(ArchiveReader reader)
    {
        long original = 0;
        long stored = 0;
        Entries.Clear();
        foreach (ArchiveEntry entry in reader.Entries)
        {
            original += entry.OriginalSize;
            stored += entry.StoredSize;
            Entries.Add(ArchiveEntryRow.From(entry));
        }

        Summary = original == 0
            ? L.Format("Archive.Summary", reader.Entries.Count, FormatSize(original), FormatSize(stored))
            : L.Format("Archive.SummaryRatio", reader.Entries.Count, FormatSize(original), FormatSize(stored),
                L.Number(stored / (double)original, "P1"));
    }

    /// <summary>Decrypts, decompresses and checks every file of the opened archive, writing nothing.</summary>
    [RelayCommand]
    private async Task VerifyAsync()
    {
        if (!CanUseArchive || _reader is not { } reader)
            return;

        (bool ok, TimeSpan elapsed) = await RunAsync("Archive.Status.Verifying", token =>
        {
            var timer = Stopwatch.StartNew();
            reader.Verify(token);
            return timer.Elapsed;
        });

        if (ok)
            SetStatus(() => L.Format("Archive.Status.Verified", reader.Entries.Count, elapsed.TotalSeconds));
    }

    /// <summary>Restores every file of the opened archive below a folder. Existing files are never replaced.</summary>
    /// <param name="directory">The folder to restore into.</param>
    public async Task ExtractAsync(string directory)
    {
        if (!CanUseArchive || _reader is not { } reader)
            return;

        (bool ok, TimeSpan elapsed) = await RunAsync("Archive.Status.Extracting", token =>
        {
            var timer = Stopwatch.StartNew();
            reader.ExtractAll(directory, token);
            return timer.Elapsed;
        });

        if (ok)
            SetStatus(() => L.Format("Archive.Status.Restored", reader.Entries.Count, directory, elapsed.TotalSeconds));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cancellation?.Cancel();
        _reader?.Dispose();
        _reader = null;
    }

    [RelayCommand]
    private void RemoveSource(string? path)
    {
        if (IsBusy || path is null)
            return;

        Sources.Remove(path);
        HasSources = Sources.Count > 0;
    }

    [RelayCommand]
    private void ClearSources()
    {
        if (IsBusy)
            return;

        Sources.Clear();
        HasSources = false;
    }

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    /// <summary>Locks the opened archive again and forgets its keys.</summary>
    [RelayCommand]
    private void Close()
    {
        if (IsBusy)
            return;

        CloseArchive();
        ArchivePath = null;
        ArchiveName = L["Archive.NoArchive"];
        SetStatus(() => L["Archive.Status.Closed"]);
    }

    private void CloseArchive()
    {
        _reader?.Dispose();
        _reader = null;
        Entries.Clear();
        Summary = string.Empty;
        IsOpen = false;
    }

    // Runs the work off the UI thread and turns every failure into Status text, so the
    // async void handlers that call in can never bring the process down.
    private async Task<(bool Ok, T Value)> RunAsync<T>(string busyStatusKey, Func<CancellationToken, T> work)
    {
        if (IsBusy)
            return (false, default!);

        IsBusy = true;
        SetStatus(() => L[busyStatusKey]);

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        try
        {
            CancellationToken token = cancellation.Token;
            return (true, await Task.Run(() => work(token), token));
        }
        catch (OperationCanceledException)
        {
            SetStatus(() => L["Archive.Status.Cancelled"]);
        }
        catch (Exception exception)
        {
            // The message comes from the runtime or from Core and stays in its language.
            SetStatus(() => L.Format("Common.Failed", exception.Message));
        }
        finally
        {
            _cancellation = null;
            IsBusy = false;
        }

        return (false, default!);
    }

    private void SetStatus(Func<string> text)
    {
        _statusText = text;
        Status = text();
    }

    // Everything on the page that was built from translated text is built again.
    private void Retranslate()
    {
        Status = _statusText();
        CodecChoices[0].Retranslate();

        if (ArchivePath is null)
            ArchiveName = L["Archive.NoArchive"];

        if (_reader is { } reader && IsOpen)
            ShowEntries(reader);
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => L.Format("Unit.Bytes", bytes),
        < 1024 * 1024 => L.Format("Unit.KiB", bytes / 1024.0),
        < 1024L * 1024 * 1024 => L.Format("Unit.MiB", bytes / (1024.0 * 1024)),
        _ => L.Format("Unit.GiB", bytes / (1024.0 * 1024 * 1024)),
    };
}

/// <summary>One file of an opened archive, formatted for the table.</summary>
/// <param name="Path">Relative path inside the archive.</param>
/// <param name="Size">Original size.</param>
/// <param name="Stored">Compressed size.</param>
/// <param name="Ratio">Compressed size as a share of the original.</param>
/// <param name="Modified">Last modification time, UTC.</param>
public sealed record ArchiveEntryRow(string Path, string Size, string Stored, string Ratio, string Modified)
{
    /// <summary>Formats an archive entry.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The table row.</returns>
    public static ArchiveEntryRow From(ArchiveEntry entry) => new(
        entry.Path,
        ArchivePageViewModel.FormatSize(entry.OriginalSize),
        ArchivePageViewModel.FormatSize(entry.StoredSize),
        entry.OriginalSize == 0 ? "—" : Localizer.Instance.Number(entry.StoredSize / (double)entry.OriginalSize, "P1"),
        entry.ModifiedUtc.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));
}
