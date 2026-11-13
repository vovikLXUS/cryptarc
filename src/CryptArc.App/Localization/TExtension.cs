using System;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace CryptArc.App.Localization;

/// <summary>
/// XAML markup extension for a translated string: <c>Text="{loc:T Shell.ChooseFile}"</c>.
/// The text changes in place when the language is switched.
/// </summary>
/// <param name="key">Resource name in <c>Strings.resx</c>.</param>
public sealed class TExtension(string key) : MarkupExtension
{
    /// <summary>Resource name in <c>Strings.resx</c>.</summary>
    public string Key { get; } = key;

    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        CompiledBinding.Create(
            (LocalizedString text) => text.Value,
            source: Localizer.Instance.Bind(Key),
            mode: BindingMode.OneWay);
}
