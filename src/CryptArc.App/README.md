# CryptArc.App

Avalonia desktop application. Like the CLI, it is a thin shell over `Core`.

## Screens

A side menu switches between pages; the file card at the top is shared by all
of them. Theme: Semi.Avalonia, charts: ScottPlot (ADR-0015).

| Page | Shows |
|---|---|
| Analyse | entropy, repeat ratio and text share as the mean over 64 KiB blocks, the codec picked for most blocks, and a heatmap of entropy per block |
| Compare codecs | every registered codec on the file: a bar chart of size or encode speed, and a sortable table |
| Compress | `BlockCompressor` on the file with a chosen codec or Auto, block size and thread count: progress, cancel, a live map of blocks coloured by codec, and a history of runs with speedup against the one-thread baseline. Nothing is written to disk until the container exists |

Planned, shown as "soon" in the menu: a transform lab (BWT → MTF → RLE step by
step), naive versus optimised, and the encrypted archive.

## Structure

| Path | Role |
|---|---|
| `Program.cs` | entry point, Avalonia app builder |
| `App.axaml(.cs)` | application-level styles and lifetime; DI composition root |
| `MainWindow.axaml` | shell: side menu, file card, content area |
| `MainWindow.axaml.cs` | code-behind; owns the file picker only |
| `Views/` | one `UserControl` per page; `CompareView` draws its chart in code-behind |
| `Converters/` | value-to-brush converters (heatmap colours) |
| `ViewModels/` | presentation state and logic, one view model per page |
| `Services/` | `ICodecMeasurement`: analysis and per-codec round trip, injected into the view model |

## Running

```bash
dotnet run --project src/CryptArc.App -c Release
dotnet run --project src/CryptArc.App -c Release -- path/to/file   # opens it straight away
```

## Dependency injection

`App.axaml.cs` builds a `Microsoft.Extensions.DependencyInjection` container and
resolves `MainWindow` from it; the window gets its view model and the view model
gets `ICodecMeasurement` through the constructor. Register new services there.
`MainWindow()` without arguments exists only for the XAML previewer. See
ADR-0014 for why this is not Prism.

## Why the file picker lives in code-behind

Opening a dialog needs a `TopLevel`, which is a view concept. Keeping it in the
window means `MainWindowViewModel` only ever receives a path, so it stays plain
and unit-testable without an Avalonia harness.
