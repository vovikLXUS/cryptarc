using System;
using System.Collections.Generic;
using System.Globalization;
using System.Resources;

namespace CryptArc.App.Localization;

/// <summary>
/// The user-interface strings in the current language, and the switch between languages.
/// </summary>
/// <remarks>
/// <para>
/// The strings live in <c>Strings.resx</c> (English, the fallback) and
/// <c>Strings.uk.resx</c> (Ukrainian). XAML reaches them through
/// <see cref="TExtension"/>, view models through <see cref="Instance"/>.
/// </para>
/// <para>
/// One static instance rather than an injected service: a XAML markup extension has
/// no constructor to inject into, and two sources of the current language would be
/// worse than one static (ADR-0016). The class has no Avalonia dependency, so view
/// models may use it.
/// </para>
/// <para>
/// Numbers follow the language too, and always through <see cref="Culture"/>:
/// <see cref="Format"/> and <see cref="Number"/> pass it explicitly. Switching also sets
/// the thread's current culture, but only for code that cannot be handed one (chart
/// axis ticks). Our own text must not rely on it: the current culture is captured by
/// every pending async continuation, which would resume in the previous language.
/// </para>
/// </remarks>
public sealed class Localizer
{
    private static readonly ResourceManager Resources =
        new("CryptArc.App.Localization.Strings", typeof(Localizer).Assembly);

    private readonly Dictionary<string, LocalizedString> _bound = [];

    private Localizer()
    {
        Language = Languages[0];
        Culture = CultureInfo.GetCultureInfo(Language.CultureName);
    }

    /// <summary>The one instance.</summary>
    public static Localizer Instance { get; } = new();

    /// <summary>Raised on the calling thread after the language has changed.</summary>
    public event EventHandler? LanguageChanged;

    /// <summary>Every language the interface is translated into; the first is the fallback.</summary>
    public IReadOnlyList<AppLanguage> Languages { get; } =
    [
        new("en", "en-US", "English"),
        new("uk", "uk-UA", "Українська"),
    ];

    /// <summary>The language in use.</summary>
    public AppLanguage Language { get; private set; }

    /// <summary>The culture of <see cref="Language"/>: picks the strings and formats the numbers.</summary>
    public CultureInfo Culture { get; private set; }

    /// <summary>The string for <paramref name="key"/> in the current language.</summary>
    /// <param name="key">Resource name, e.g. <c>Shell.ChooseFile</c>.</param>
    /// <returns>The string, or the key itself when no resource has that name, so a typo shows on screen.</returns>
    public string this[string key] => Resources.GetString(key, Culture) ?? key;

    /// <summary>The string for a key that may not exist, e.g. one built from a benchmark class name.</summary>
    /// <param name="key">Resource name.</param>
    /// <returns>The string in the current language, or null when no resource has that name.</returns>
    public string? TryGet(string key) => Resources.GetString(key, Culture);

    /// <summary>The string for <paramref name="key"/> with its <c>{0}</c> placeholders filled in.</summary>
    /// <param name="key">Resource name.</param>
    /// <param name="arguments">Values for the placeholders, formatted in the current culture.</param>
    /// <returns>The formatted string.</returns>
    public string Format(string key, params object?[] arguments) =>
        string.Format(Culture, this[key], arguments);

    /// <summary>A number as text in the current culture, e.g. <c>Number(0.42, "P1")</c>.</summary>
    /// <param name="value">The number.</param>
    /// <param name="format">A standard or custom numeric format string.</param>
    /// <returns>The formatted number.</returns>
    public string Number(double value, string format) => value.ToString(format, Culture);

    /// <summary>The language the interface starts in when the user has not chosen one.</summary>
    /// <param name="system">The operating system's interface culture.</param>
    /// <returns>The language with the same two-letter code, or the fallback.</returns>
    public AppLanguage Match(CultureInfo system) =>
        FindLanguage(system.TwoLetterISOLanguageName) ?? Languages[0];

    /// <summary>The language with the given code.</summary>
    /// <param name="code">Two-letter code, e.g. <c>uk</c>.</param>
    /// <returns>The language, or null if the interface is not translated into it.</returns>
    public AppLanguage? FindLanguage(string? code)
    {
        foreach (AppLanguage language in Languages)
        {
            if (language.Code == code)
                return language;
        }

        return null;
    }

    /// <summary>Switches the interface to <paramref name="language"/>. Call on the UI thread.</summary>
    /// <param name="language">One of <see cref="Languages"/>.</param>
    public void Use(AppLanguage language)
    {
        bool changed = language != Language;

        Language = language;
        Culture = CultureInfo.GetCultureInfo(language.CultureName);
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.CurrentCulture = Culture;
        CultureInfo.CurrentUICulture = Culture;

        if (!changed)
            return;

        foreach (LocalizedString text in _bound.Values)
            text.Refresh();

        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The live string for <paramref name="key"/>: its <see cref="LocalizedString.Value"/>
    /// changes with the language. One object per key, shared by every binding to it.
    /// </summary>
    /// <param name="key">Resource name.</param>
    /// <returns>The shared live string.</returns>
    public LocalizedString Bind(string key)
    {
        if (!_bound.TryGetValue(key, out LocalizedString? text))
            _bound[key] = text = new LocalizedString(this, key);

        return text;
    }
}

/// <summary>A language of the interface.</summary>
/// <param name="Code">Two-letter ISO 639-1 code; also what is saved between runs.</param>
/// <param name="CultureName">Culture used for the strings and for number formats.</param>
/// <param name="NativeName">The language's own name, shown in the switch whatever language is active.</param>
public sealed record AppLanguage(string Code, string CultureName, string NativeName)
{
    /// <inheritdoc />
    public override string ToString() => NativeName;
}
