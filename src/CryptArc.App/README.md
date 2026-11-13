# CryptArc.App

Avalonia desktop application. Like the CLI, it is a thin shell over `Core`.

## Screens

A side menu switches between pages; the file card at the top is shared by all
of them. Theme: Semi.Avalonia, charts: ScottPlot (ADR-0015).

The interface is in English and Ukrainian. The switch is at the bottom of the
side menu and takes effect at once, without a restart; see
[Languages](#languages) below.

| Page | Shows |
|---|---|
| Analyse | entropy, repeat ratio and text share as the mean over 64 KiB blocks, the codec picked for most blocks, a heatmap of entropy per block, and a histogram of every byte value over the whole file |
| Compare codecs | every registered codec on the file: a bar chart of size or encode speed, and a sortable table |
| Compress | `BlockCompressor` on the file with a chosen codec or Auto, block size and thread count: progress, cancel, a live map of blocks coloured by codec, and a history of runs with speedup against the one-thread baseline. Nothing is written to disk until the container exists |

| Transform lab | a short text through the stages of the BWT codec: the sorted rotation matrix and its last column, move-to-front, zero-run coding, and the Huffman code of the result next to Huffman on the raw text. Needs no file |

| Naive vs optimised | the optimisation pairs of hard rule 4 as measured by BenchmarkDotNet in Release: speedup chart, times and allocations per case, and the machine. Reads `bench/results/*.pairs.json`, copied beside the executable at build time; the app times nothing here |

Planned, shown as "soon" in the menu: the encrypted archive.

## Structure

| Path | Role |
|---|---|
| `Program.cs` | entry point, Avalonia app builder |
| `App.axaml(.cs)` | application-level styles and lifetime; DI composition root |
| `MainWindow.axaml` | shell: side menu, file card, content area |
| `MainWindow.axaml.cs` | code-behind; owns the file picker only |
| `Views/` | one `UserControl` per page; `CompareView` draws its chart in code-behind |
| `Converters/` | value-to-brush converters (heatmap colours) |
| `Localization/` | `Strings.resx` (English) and `Strings.uk.resx` (Ukrainian), `Localizer`, and the `{loc:T Key}` markup extension |
| `ViewModels/` | presentation state and logic, one view model per page |
| `Services/` | `ICodecMeasurement`: analysis and per-codec round trip, injected into the view model; `TransformLab`: the BWT codec's stages run one by one for the lab; `IBenchmarkResults`: reads the committed benchmark results; `ILanguagePreference`: remembers the chosen language |

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

## Languages

Every string the user sees comes from `Localization/Strings.resx` (English, the
fallback) or `Strings.uk.resx` (Ukrainian), by key. See ADR-0016.

- In XAML: `Text="{loc:T Shell.ChooseFile}"`. The binding follows the language.
- In a view model: `Localizer.Instance["Key"]`, or `Format("Key", args)` when the
  string has `{0}` placeholders. A view model that keeps such text in a property
  subscribes to `Localizer.LanguageChanged` and builds the text again.
- Numbers go through `Localizer.Format` or `Localizer.Number`, which use the
  language's culture, never through a bare `$"{x:N0}"`.
- The first start follows the operating system's language; after that the choice
  is read from `CryptArc/language` in the user's application-data folder.

To add a string, add the key to **both** files. To add a language, add
`Strings.<code>.resx` and one entry to `Localizer.Languages`.
`LocalizationResourcesTests` fails when a key is missing from one file, when a
translation drops a placeholder, or when a view has literal text.

Not translated: codec names (they come from `CodecRegistry`), method and class
names from the benchmark results, and exception messages from the runtime.
