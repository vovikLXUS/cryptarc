# CryptArc.App

- **No algorithm logic.** Compression, analysis and crypto belong in `Core`.
- **Never block the UI thread.** Anything over a few milliseconds goes through
  `Task.Run`; update observable state afterwards. A file dialog on a 2 GB file
  must not freeze the window.
- MVVM via `CommunityToolkit.Mvvm` (`[ObservableProperty]`, `[RelayCommand]`).
  Do not add ReactiveUI alongside it.
- **Avalonia types stay out of `ViewModels/`.** No `Window`, no `TopLevel`, no
  `IStorageProvider` in a view model — dialogs live in code-behind.
- Bindings are compiled: keep `x:DataType` on every `Window` and `DataTemplate`.
- The GUI enumerates `CodecRegistry`. Do not hard-code algorithm names in XAML.
- Passwords, once added: `char[]` or `SecureString`, cleared with `Array.Clear`
  after key derivation. Never log them, never put them in an observable property
  that survives the dialog.
