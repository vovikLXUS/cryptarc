using System.Threading.Tasks;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CryptArc.App.Services;
using CryptArc.App.ViewModels;

namespace CryptArc.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    /// <summary>Runtime constructor: the container supplies the view model.</summary>
    public MainWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = _viewModel;
        ChooseFileButton.Click += OnChooseFileClick;
    }

    /// <summary>
    /// Design-time constructor for the XAML previewer and the runtime loader, which
    /// need a public parameterless one. The application itself never calls it.
    /// </summary>
    public MainWindow()
        : this(new MainWindowViewModel(new CodecMeasurement(), new AnalysePageViewModel(), new ComparePageViewModel(), new CompressPageViewModel(), new TransformLabPageViewModel(), new NaiveVsOptimisedPageViewModel(new BenchmarkResults()), new ArchivePageViewModel()))
    {
    }

    /// <summary>Loads a file as if it had been picked in the dialog.</summary>
    /// <param name="path">Local path of the file.</param>
    public Task OpenAsync(string path) => _viewModel.LoadAsync(path);

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
