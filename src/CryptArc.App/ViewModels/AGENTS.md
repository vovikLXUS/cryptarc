# ViewModels

- **No Avalonia types in this folder.** No `Window`, `TopLevel`, `Control`,
  `IStorageProvider`. A view model takes primitives and returns state.
- Use `[ObservableProperty]` on private fields; the generator creates the public
  property. Do not hand-write `INotifyPropertyChanged` plumbing.
- **Off-thread work, on-thread assignment**: compute in `Task.Run`, assign
  observable properties afterwards. Never mutate an `ObservableCollection` from
  a background thread.
- Guard re-entrancy with `IsBusy` so a second click cannot start a second run.
- Catch exceptions and surface them as `Status` text — an unhandled exception in
  an `async void` event handler kills the process.
- **Text kept in a property must survive a language switch.** Keep what the
  text was built from and rebuild it on `Localizer.LanguageChanged`; a string
  formatted once stays in the old language. `Localizer` is plain C#, not an
  Avalonia type, so using it here is allowed.
