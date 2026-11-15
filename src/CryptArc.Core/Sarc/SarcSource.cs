namespace CryptArc.Core.Sarc;

/// <summary>A file to be packed into a SARC archive.</summary>
/// <param name="Name">The plain file name stored in the archive; version 1 has no paths.</param>
/// <param name="Data">The file's contents.</param>
public sealed record SarcSource(string Name, ReadOnlyMemory<byte> Data)
{
    /// <summary>
    /// Whether a name may be stored: not empty, not <c>.</c> or <c>..</c>, and free of
    /// <c>/</c>, <c>\</c>, <c>:</c> and NUL (FORMAT_SPEC_P2 §2.2). Names that are legal on
    /// the local file system can still fail this, for example <c>notes: draft.txt</c> on macOS.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>Whether the writer accepts it.</returns>
    public static bool IsValidName(string name) => SarcPayload.IsSafeName(name);

    /// <summary>
    /// Reads files from disk. A file becomes one source named after it; a directory gives
    /// every file below it, symbolic links skipped, each under its bare file name.
    /// </summary>
    /// <param name="paths">Files and directories.</param>
    /// <returns>The sources in the order found.</returns>
    /// <exception cref="FileNotFoundException">A path does not exist.</exception>
    public static IReadOnlyList<SarcSource> FromPaths(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var sources = new List<SarcSource>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
        };

        foreach (string path in paths)
        {
            if (File.Exists(path))
            {
                sources.Add(FromFile(path));
                continue;
            }

            if (!Directory.Exists(path))
                throw new FileNotFoundException($"No such file or directory: {path}", path);

            foreach (string file in Directory.EnumerateFiles(path, "*", options))
                sources.Add(FromFile(file));
        }

        return sources;
    }

    private static SarcSource FromFile(string path) => new(Path.GetFileName(path), File.ReadAllBytes(path));
}
