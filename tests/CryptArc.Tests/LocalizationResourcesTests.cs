using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CryptArc.Tests;

/// <summary>
/// The GUI's translated strings (ADR-0016), checked on the source files: a key is a
/// plain string, so nothing at compile time notices a typo or a string that exists in
/// one language only. Read from disk on purpose: the test project references
/// <c>CryptArc.Core</c> alone and must not pull in Avalonia to check two XML files.
/// </summary>
public partial class LocalizationResourcesTests
{
    private static readonly string AppFolder = Path.Combine(RepositoryRoot(), "src", "CryptArc.App");

    private static readonly Dictionary<string, string> English = Load("Strings.resx");
    private static readonly Dictionary<string, string> Ukrainian = Load("Strings.uk.resx");

    [Fact]
    public void BothLanguages_HaveTheSameKeys()
    {
        string[] onlyEnglish = English.Keys.Except(Ukrainian.Keys).Order().ToArray();
        string[] onlyUkrainian = Ukrainian.Keys.Except(English.Keys).Order().ToArray();

        Assert.True(onlyEnglish.Length == 0, $"Missing from Strings.uk.resx: {string.Join(", ", onlyEnglish)}");
        Assert.True(onlyUkrainian.Length == 0, $"Missing from Strings.resx: {string.Join(", ", onlyUkrainian)}");
    }

    [Fact]
    public void NoString_IsEmpty()
    {
        string[] empty = English.Concat(Ukrainian)
            .Where(pair => string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => pair.Key)
            .Distinct()
            .ToArray();

        Assert.True(empty.Length == 0, $"Empty strings: {string.Join(", ", empty)}");
    }

    // "{0:N0} bytes" and its translation must take the same arguments in the same
    // formats, or one language throws FormatException or shows a different number.
    [Fact]
    public void Translation_KeepsEveryPlaceholder()
    {
        string[] different = English.Keys
            .Where(Ukrainian.ContainsKey)
            .Where(key => !Placeholders(English[key]).SequenceEqual(Placeholders(Ukrainian[key])))
            .ToArray();

        Assert.True(different.Length == 0, $"Placeholders differ between the languages: {string.Join(", ", different)}");
    }

    [Fact]
    public void EveryKeyUsedInTheApp_Exists()
    {
        var missing = new SortedSet<string>();

        foreach (string path in SourceFiles("*.axaml"))
            foreach (Match match in XamlKey().Matches(File.ReadAllText(path)))
                Check(match, path);

        foreach (string path in SourceFiles("*.cs"))
            foreach (Match match in CodeKey().Matches(File.ReadAllText(path)))
                Check(match, path);

        Assert.True(missing.Count == 0, $"Keys with no string in Strings.resx: {string.Join("; ", missing)}");

        void Check(Match match, string path)
        {
            string key = match.Groups[1].Value;
            if (!English.ContainsKey(key))
                missing.Add($"{key} ({Path.GetFileName(path)})");
        }
    }

    // A literal Text="…" in a view is a string that will not follow the language.
    [Fact]
    public void Views_HaveNoUntranslatedText()
    {
        var literals = new SortedSet<string>();

        foreach (string path in SourceFiles("*.axaml"))
        {
            string xaml = XamlComment().Replace(File.ReadAllText(path), string.Empty);
            foreach (Match match in LiteralText().Matches(xaml).Concat(ElementText().Matches(xaml)))
                if (match.Groups[1].Value != "CryptArc") // the product name is the same in every language
                    literals.Add($"\"{match.Groups[1].Value}\" ({Path.GetFileName(path)})");
        }

        // Code-behind owns the dialogs, and a dialog title is user-facing text too.
        foreach (string path in SourceFiles("*.axaml.cs"))
            foreach (Match match in DialogTitle().Matches(File.ReadAllText(path)))
                literals.Add($"\"{match.Groups[1].Value}\" ({Path.GetFileName(path)})");

        Assert.True(literals.Count == 0, $"Use {{loc:T Key}} instead of: {string.Join("; ", literals)}");
    }

    private static Dictionary<string, string> Load(string file) =>
        XDocument.Load(Path.Combine(AppFolder, "Localization", file))
            .Root!
            .Elements("data")
            .ToDictionary(data => data.Attribute("name")!.Value, data => data.Element("value")!.Value);

    private static string[] Placeholders(string text) =>
        Placeholder().Matches(text).Select(match => match.Value).Order(StringComparer.Ordinal).ToArray();

    private static IEnumerable<string> SourceFiles(string pattern) =>
        Directory.EnumerateFiles(AppFolder, pattern, SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "CryptArc.slnx")))
                return folder.FullName;
        }

        throw new DirectoryNotFoundException("CryptArc.slnx not found above the test assembly.");
    }

    // {0}, {0:N1}, {0,5} and {0,-5:N1}: alignment is part of what a translation must keep.
    [GeneratedRegex(@"\{[0-9]+(?:,[^}:]*)?(?::[^}]*)?\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\{loc:T\s+([A-Za-z0-9.]+)\}")]
    private static partial Regex XamlKey();

    // L["Key"], L.Format("Key", …), Localizer.Instance["Key"] and the like. Keys built
    // at run time (the benchmark suite names) are not literals and are not seen here.
    [GeneratedRegex(@"\b(?:L|l|Instance)(?:\[|\.Format\(|\.TryGet\()\s*""([A-Za-z0-9.]+)""")]
    private static partial Regex CodeKey();

    [GeneratedRegex(@"\b(?:Text|Content|Header|Watermark|PlaceholderText|Title|ToolTip\.Tip)=""([^""{]*\p{L}{2}[^""]*)""")]
    private static partial Regex LiteralText();

    // Text written between the tags: <TextBlock>Some text</TextBlock>.
    [GeneratedRegex(@"<(?:TextBlock|SelectableTextBlock|Run|Label|Button|CheckBox|RadioButton|ToggleButton)\b[^>]*(?<!/)>\s*([^<{]*\p{L}{2}[^<]*?)\s*<")]
    private static partial Regex ElementText();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XamlComment();

    // A dialog title set in code-behind: Title = "Choose a file".
    [GeneratedRegex(@"\bTitle\s*=\s*""([^""]*\p{L}{2}[^""]*)""")]
    private static partial Regex DialogTitle();
}
