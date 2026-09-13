# CryptArc.App

Avalonia desktop application. Like the CLI, it is a thin shell over `Core`.

## Current screen

Pick a file and the window shows its size, CRC-32, entropy, repeat and text
ratios, the codec the analyzer would choose, and a table comparing every
implemented codec by size, ratio and throughput.

Planned: pack/unpack screens, password entry, progress and cancellation.

## Structure

| Path | Role |
|---|---|
| `Program.cs` | entry point, Avalonia app builder |
| `App.axaml(.cs)` | application-level styles and lifetime |
| `MainWindow.axaml` | layout |
| `MainWindow.axaml.cs` | code-behind; owns the file picker only |
| `ViewModels/` | presentation state and logic |

## Running

```bash
dotnet run --project src/CryptArc.App -c Release
```

## Why the file picker lives in code-behind

Opening a dialog needs a `TopLevel`, which is a view concept. Keeping it in the
window means `MainWindowViewModel` only ever receives a path, so it stays plain
and unit-testable without an Avalonia harness.
