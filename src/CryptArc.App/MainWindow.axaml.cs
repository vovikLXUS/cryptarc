using System.Threading.Tasks;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CryptArc.App.Services;
using CryptArc.App.ViewModels;

namespace CryptArc.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    // The file of the drag that is over the window now; null when there is none or it is not a file.
    private string? _draggedPath;

    /// <summary>Runtime constructor: the container supplies the view model.</summary>
    public MainWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = _viewModel;
        ChooseFileButton.Click += OnChooseFileClick;

        // DragDrop.AllowDrop is set on the window in XAML, so a file can be dropped
        // anywhere on it, whichever page is open.
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
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

    // Dropping is the picker without the dialog: the view model still only sees a path.
    //
    // The path is worked out once, when the drag enters, and kept until it leaves or
    // is dropped. DragOver arrives on every mouse move, and asking the drag for its
    // files each time would allocate a new set of storage items per move.
    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        _draggedPath = DroppedFilePath(e);
        OnDragOver(sender, e);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        bool accept = !_viewModel.IsBusy && _draggedPath is not null;

        e.DragEffects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.IsVisible = accept;
        e.Handled = true;
    }

    // Also raised when the pointer moves from one control to another inside the
    // window; DragEnter follows at once and works the path out again.
    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        _draggedPath = null;
        DropOverlay.IsVisible = false;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        string? path = _draggedPath;
        _draggedPath = null;
        DropOverlay.IsVisible = false;
        e.Handled = true;

        // The effect is what the source application is told happened. Left alone it
        // stays "everything the source allows", which reports a refused drop as done
        // and an opened file as possibly moved.
        if (_viewModel.IsBusy || path is null)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;

        // The drag came from another application, which still has the focus.
        Activate();
        await _viewModel.LoadAsync(path);
    }

    // The first local file of the drag, skipping folders. A drag with no file on
    // disk (only folders, text, a browser image) gives null, and the drop is refused.
    private static string? DroppedFilePath(DragEventArgs e)
    {
        IStorageItem[]? items = e.DataTransfer.TryGetFiles();
        if (items is null)
            return null;

        foreach (IStorageItem item in items)
        {
            if (item is IStorageFile && item.TryGetLocalPath() is { } path)
                return path;
        }

        return null;
    }
}
