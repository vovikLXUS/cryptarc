using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CryptArc.App.ViewModels;

namespace CryptArc.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        ChooseFileButton.Click += OnChooseFileClick;
    }

    // File dialogs are a view concern, so the picker lives here and the view model
    // only ever sees a path. Keeps MainWindowViewModel unit-testable.
    private async void OnChooseFileClick(object? sender, RoutedEventArgs e)
    {
        IStorageProvider? storage = GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a file to analyse",
            AllowMultiple = false,
        });

        if (files.Count == 0)
            return;

        string? path = files[0].TryGetLocalPath();
        if (path is not null)
            await _viewModel.LoadAsync(path);
    }
}
