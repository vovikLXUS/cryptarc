using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CryptArc.App.Localization;
using CryptArc.App.ViewModels;

namespace CryptArc.App.Views;

/// <summary>
/// The "Encrypted archive" page. The dialogs and the password boxes are view
/// concerns and live here: the view model only ever receives paths, and a password
/// only as an argument, after which the box it was typed into is emptied.
/// </summary>
public partial class ArchiveView : UserControl
{
    private const string Extension = "cryptarc";

    // Built for each dialog rather than once: its name follows the language.
    private static FilePickerFileType ArchiveType => new(Localizer.Instance["Archive.Dialog.FileType"])
    {
        Patterns = [$"*.{Extension}"],
    };

    /// <summary>Creates the view; the shell's data template supplies the view model.</summary>
    public ArchiveView()
    {
        InitializeComponent();

        AddFilesButton.Click += OnAddFilesClick;
        AddFolderButton.Click += OnAddFolderClick;
        CreateButton.Click += OnCreateClick;
        ChooseArchiveButton.Click += OnChooseArchiveClick;
        UnlockButton.Click += OnUnlockClick;
        OpenPasswordBox.KeyDown += OnOpenPasswordKeyDown;
        ExtractButton.Click += OnExtractClick;
    }

    private ArchivePageViewModel? ViewModel => DataContext as ArchivePageViewModel;

    private IStorageProvider? Storage => TopLevel.GetTopLevel(this)?.StorageProvider;

    private static List<string> LocalPaths(IReadOnlyList<IStorageItem> items)
    {
        var paths = new List<string>(items.Count);
        foreach (IStorageItem item in items)
        {
            if (item.TryGetLocalPath() is { } path)
                paths.Add(path);
        }

        return paths;
    }

    private async void OnAddFilesClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || Storage is not { } storage)
            return;

        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Instance["Archive.Dialog.AddFiles"],
            AllowMultiple = true,
        });

        viewModel.AddSources(LocalPaths(files));
    }

    private async void OnAddFolderClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || Storage is not { } storage)
            return;

        IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Localizer.Instance["Archive.Dialog.AddFolders"],
            AllowMultiple = true,
        });

        viewModel.AddSources(LocalPaths(folders));
    }

    private async void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || Storage is not { } storage)
            return;

        if (!viewModel.ValidateNewPassword(NewPasswordBox.Text, RepeatPasswordBox.Text))
            return;

        IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Localizer.Instance["Archive.Dialog.Create"],
            SuggestedFileName = SuggestedName(viewModel),
            DefaultExtension = Extension,
            FileTypeChoices = [ArchiveType],
        });

        if (file?.TryGetLocalPath() is not { } path)
            return;

        string password = NewPasswordBox.Text ?? string.Empty;
        NewPasswordBox.Clear();
        RepeatPasswordBox.Clear();

        await viewModel.CreateAsync(path, password);
    }

    private static string SuggestedName(ArchivePageViewModel viewModel)
    {
        string first = viewModel.Sources.Count > 0
            ? Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(viewModel.Sources[0]))
            : string.Empty;

        return $"{(first.Length > 0 ? first : "archive")}.{Extension}";
    }

    private async void OnChooseArchiveClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || Storage is not { } storage)
            return;

        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Instance["Archive.Dialog.Choose"],
            AllowMultiple = false,
            FileTypeFilter = [ArchiveType, FilePickerFileTypes.All],
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            return;

        OpenPasswordBox.Clear();
        viewModel.SelectArchive(path);
        OpenPasswordBox.Focus();
    }

    private async void OnUnlockClick(object? sender, RoutedEventArgs e) => await UnlockAsync();

    private async void OnOpenPasswordKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        await UnlockAsync();
    }

    private async System.Threading.Tasks.Task UnlockAsync()
    {
        if (ViewModel is not { CanUnlock: true } viewModel)
            return;

        string? password = OpenPasswordBox.Text;
        OpenPasswordBox.Clear();

        await viewModel.UnlockAsync(password);
    }

    private async void OnExtractClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || Storage is not { } storage)
            return;

        IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Localizer.Instance["Archive.Dialog.ExtractTo"],
            AllowMultiple = false,
        });

        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } directory)
            return;

        await viewModel.ExtractAsync(directory);
    }
}
