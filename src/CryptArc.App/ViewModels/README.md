# ViewModels

Presentation state for the Avalonia UI.

| Type | Role |
|---|---|
| `MainWindowViewModel` | the shell: selected page, loaded file, loading off the UI thread |
| `AnalysePageViewModel` | summary cards and heatmap cells for the "Analyse" page |
| `ComparePageViewModel` | codec rows and the size/speed switch for the "Compare codecs" page |
| `CompressPageViewModel` | settings, run and cancel, block map and run history for the "Compress" page |
| `BlockTile`, `CodecUsage`, `RunRecord`, `CodecChoice` | the Compress page's map tiles, legend, history rows and codec list |
| `FileSummary` | whole-file figures aggregated over block profiles |
| `HeatmapCell` | one heatmap square: entropy and tooltip |
| `CodecResult` | one row of the codec comparison table, with display-formatted strings |

## Rules of thumb

- These classes know nothing about windows or dialogs. `MainWindowViewModel`
  takes a **path**, not an `IStorageFile`, which is why it can be unit-tested
  without starting Avalonia.
- Heavy work happens inside `Task.Run` (`ICodecMeasurement.Measure`, injected
  through the constructor), and only the resulting values are assigned back to
  observable properties.
- Formatting for display (`"85.2 %"`, `"4 044 B"`) belongs on the view model, not
  in XAML converters and not in `Core`. Colours are the exception: a brush is an
  Avalonia type, so the view model exposes the number and a converter in
  `Converters/` picks the colour.
- Whole-file numbers come from `FileSummary`, not from `DataAnalyzer.Analyze` on
  the whole buffer: the analyzer samples only the first 64 KiB.
