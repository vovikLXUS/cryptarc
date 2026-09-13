# ViewModels

Presentation state for the Avalonia UI.

| Type | Role |
|---|---|
| `MainWindowViewModel` | loads a file, runs analysis off the UI thread, exposes the results |
| `CodecResult` | one row of the codec comparison table, with display-formatted strings |

## Rules of thumb

- These classes know nothing about windows or dialogs. `MainWindowViewModel`
  takes a **path**, not an `IStorageFile`, which is why it can be unit-tested
  without starting Avalonia.
- Heavy work happens inside `Task.Run` (`Measure`), and only the resulting
  values are assigned back to observable properties.
- Formatting for display (`"85.2 %"`, `"4 044 B"`) belongs on the view model, not
  in XAML converters and not in `Core`.
