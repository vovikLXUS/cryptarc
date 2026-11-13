using System;
using System.IO;

namespace CryptArc.App.Services;

/// <summary>Remembers the interface language between runs.</summary>
public interface ILanguagePreference
{
    /// <summary>The saved language code.</summary>
    /// <returns>A two-letter code such as <c>uk</c>, or null if nothing is saved.</returns>
    string? Load();

    /// <summary>Saves the language code. A failure to write is ignored: the choice is a convenience.</summary>
    /// <param name="code">Two-letter language code.</param>
    void Save(string code);
}

/// <summary>
/// Default <see cref="ILanguagePreference"/>: a one-line text file in the user's
/// application-data folder (<c>CryptArc/language</c>).
/// </summary>
public sealed class LanguagePreference : ILanguagePreference
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CryptArc", "language");

    /// <inheritdoc />
    public string? Load()
    {
        try
        {
            return File.Exists(_path) ? File.ReadAllText(_path).Trim() : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Save(string code)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, code);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing to do: the language still applies for this run.
        }
    }
}
