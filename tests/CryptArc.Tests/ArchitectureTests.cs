using System.Reflection;
using CryptArc.Core;

namespace CryptArc.Tests;

/// <summary>
/// Hard rules from AGENTS.md that the compiler cannot see, checked on the built
/// <c>CryptArc.Core</c> assembly: it must not pull in a ready-made compressor
/// (ADR-0002) or anything from a user interface (ADR-0011).
/// </summary>
public class ArchitectureTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        // Ready-made compression. Allowed in bench/ only, as a baseline.
        "System.IO.Compression",
        "ICSharpCode.SharpZipLib",
        "SharpCompress",
        "DotNetZip",
        "Ionic.Zip",
        "ZstdNet",
        "ZstdSharp",
        "K4os.Compression",
        "Snappier",
        "EasyCompressor",
        // User interface: Core must stay usable from any front end.
        "Avalonia",
        "System.Console",
        "System.Windows",
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase",
    ];

    [Fact]
    public void CoreReferencesNoCompressionOrUiAssembly()
    {
        Assembly core = typeof(ICodec).Assembly;

        string[] offending = core.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => ForbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.True(offending.Length == 0,
            $"CryptArc.Core references {string.Join(", ", offending)}. See AGENTS.md hard rules 1 and 5.");
    }
}
