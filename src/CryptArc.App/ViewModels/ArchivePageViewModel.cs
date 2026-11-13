using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private ArchiveReader? _reader;
    private CancellationTokenSource? _cancellation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanCreate), nameof(CanUnlock), nameof(CanUseArchive))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "Add files to pack, or choose an archive to open.";

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
    private string _archiveName = "No archive chosen";

    /// <summary>The chosen archive has been unlocked and its file table is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUnlock), nameof(CanUseArchive), nameof(IsLocked))]
    private bool _isOpen;

    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>Creates the page with "Auto" selected.</summary>
    public ArchivePageViewModel()
    {
        var choices = new List<CodecChoice> { new("Auto (per block)", null) };
        foreach (ICodec codec in CodecRegistry.All)
            choices.Add(new CodecChoice(codec.Name, codec.Id));

        CodecChoices = choices;
        _selectedCodec = choices[0];
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
            Status = "Enter a password for the new archive.";
            return false;
        }

        if (!string.Equals(password, repeat, StringComparison.Ordinal))
        {
            Status = "The passwords do not match.";
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

        (bool ok, (int Files, long Size, TimeSpan Elapsed) result) = await RunAsync("Deriving the key and packing…", token =>
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
            Status = $"Created {Path.GetFileName(archivePath)}: {result.Files:N0} files, {FormatSize(result.Size)} in {result.Elapsed.TotalSeconds:F1} s.";
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
        Status = "Enter the password and press Unlock.";
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
            Status = "Enter the password.";
            return;
        }

        (bool ok, ArchiveReader reader) = await RunAsync(
            "Deriving the key and checking integrity…",
            token => ArchiveReader.OpenFile(path, password, token));

        if (!ok)
            return;

        _reader = reader;

        long original = 0;
        long stored = 0;
        Entries.Clear();
        foreach (ArchiveEntry entry in reader.Entries)
        {
            original += entry.OriginalSize;
            stored += entry.StoredSize;
            Entries.Add(ArchiveEntryRow.From(entry));
        }

        string ratio = original == 0 ? string.Empty : $" ({stored / (double)original:P1})";
        Summary = $"{reader.Entries.Count:N0} files · {FormatSize(original)} original · {FormatSize(stored)} stored{ratio}";
        IsOpen = true;
        Status = "Unlocked: the archive passed its integrity check.";
    }

    /// <summary>Decrypts, decompresses and checks every file of the opened archive, writing nothing.</summary>
    [RelayCommand]
    private async Task VerifyAsync()
    {
        if (!CanUseArchive || _reader is not { } reader)
            return;

        (bool ok, TimeSpan elapsed) = await RunAsync("Verifying every block…", token =>
        {
            var timer = Stopwatch.StartNew();
            reader.Verify(token);
            return timer.Elapsed;
        });

        if (ok)
            Status = $"Verified: all {reader.Entries.Count:N0} files decode and match their checksums ({elapsed.TotalSeconds:F1} s).";
    }

    /// <summary>Restores every file of the opened archive below a folder. Existing files are never replaced.</summary>
    /// <param name="directory">The folder to restore into.</param>
    public async Task ExtractAsync(string directory)
    {
        if (!CanUseArchive || _reader is not { } reader)
            return;

        (bool ok, TimeSpan elapsed) = await RunAsync("Extracting…", token =>
        {
            var timer = Stopwatch.StartNew();
            reader.ExtractAll(directory, token);
            return timer.Elapsed;
        });

        if (ok)
            Status = $"Restored {reader.Entries.Count:N0} files to {directory} ({elapsed.TotalSeconds:F1} s).";
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
        ArchiveName = "No archive chosen";
        Status = "Archive closed.";
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
    private async Task<(bool Ok, T Value)> RunAsync<T>(string busyStatus, Func<CancellationToken, T> work)
    {
        if (IsBusy)
            return (false, default!);

        IsBusy = true;
        Status = busyStatus;

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        try
        {
            CancellationToken token = cancellation.Token;
            return (true, await Task.Run(() => work(token), token));
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception exception)
        {
            Status = $"Failed: {exception.Message}";
        }
        finally
        {
            _cancellation = null;
            IsBusy = false;
        }

        return (false, default!);
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes:N0} B",
        < 1024 * 1024 => $"{bytes / 1024.0:N1} KiB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):N1} MiB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):N2} GiB",
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
        entry.OriginalSize == 0 ? "—" : $"{entry.StoredSize / (double)entry.OriginalSize:P1}",
        $"{entry.ModifiedUtc:yyyy-MM-dd HH:mm}");
}
