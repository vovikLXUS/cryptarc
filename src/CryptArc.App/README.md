# CryptArc.App

Avalonia desktop application. Like the CLI, it is a thin shell over `Core`.

## Screens

A side menu switches between pages; the file card at the top is shared by all
of them. Theme: Semi.Avalonia, charts: ScottPlot (ADR-0015).

| Page | Shows |
|---|---|
| Analyse | entropy, repeat ratio and text share as the mean over 64 KiB blocks, the codec picked for most blocks, a heatmap of entropy per block, and a histogram of every byte value over the whole file |
| Compare codecs | every registered codec on the file: a bar chart of size or encode speed, and a sortable table |
| Compress | `BlockCompressor` on the file with a chosen codec or Auto, block size and thread count: progress, cancel, a live map of blocks coloured by codec, and a history of runs with speedup against the one-thread baseline. Nothing is written to disk here |
| Transform lab | a short text through the stages of the BWT codec: the sorted rotation matrix and its last column, move-to-front, zero-run coding, and the Huffman code of the result next to Huffman on the raw text. Needs no file |
| Naive vs optimised | the optimisation pairs of hard rule 4 as measured by BenchmarkDotNet in Release: speedup chart, times and allocations per case, and the machine. Reads `bench/results/*.pairs.json`, copied beside the executable at build time; the app times nothing here |
| Encrypted archive | create a `.cryptarc` archive from files and folders with a codec or Auto, a thread count and a password; open one to see its file table, verify every block and extract everything. Needs no loaded file; opening the app on a `.cryptarc` file lands here |

## Structure

| Path | Role |
|---|---|
| `Program.cs` | entry point, Avalonia app builder |
| `App.axaml(.cs)` | application-level styles and lifetime; DI composition root |
| `MainWindow.axaml` | shell: side menu, file card, content area |
| `MainWindow.axaml.cs` | code-behind; owns the file picker only |
| `Views/ArchiveView.axaml.cs` | the archive page's file, folder and save dialogs, and its password boxes |
| `Views/` | one `UserControl` per page; `CompareView` draws its chart in code-behind |
| `Converters/` | value-to-brush converters (heatmap colours) |
| `ViewModels/` | presentation state and logic, one view model per page |
| `Services/` | `ICodecMeasurement`: analysis and per-codec round trip, injected into the view model; `TransformLab`: the BWT codec's stages run one by one for the lab; `IBenchmarkResults`: reads the committed benchmark results |

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
