using System.ComponentModel;

namespace CryptArc.App.Localization;

/// <summary>
/// One interface string that follows the language: what a XAML binding made by
/// <see cref="TExtension"/> listens to.
/// </summary>
/// <remarks>
/// Views come and go as pages are switched, so they must not subscribe to
/// <see cref="Localizer.LanguageChanged"/> through a binding: the static event would
/// keep every view alive. A binding subscribes to <see cref="PropertyChanged"/> weakly,
/// and this object, one per key, is the only thing the localizer holds.
/// </remarks>
public sealed class LocalizedString : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs ValueChanged = new(nameof(Value));

    private readonly Localizer _localizer;
    private readonly string _key;

    internal LocalizedString(Localizer localizer, string key)
    {
        _localizer = localizer;
        _key = key;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The string in the current language.</summary>
    public string Value => _localizer[_key];

    internal void Refresh() => PropertyChanged?.Invoke(this, ValueChanged);
}
