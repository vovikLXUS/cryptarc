namespace CryptArc.Core.Container;

/// <summary>A file to be added to an archive.</summary>
/// <param name="ArchivePath">Relative path inside the archive, separated by '/'.</param>
/// <param name="OpenRead">Opens the contents; the writer disposes the stream.</param>
/// <param name="ModifiedUtc">Last modification time, in UTC.</param>
/// <param name="Attributes">File attributes to record.</param>
public sealed record ArchiveSource(
    string ArchivePath,
    Func<Stream> OpenRead,
    DateTime ModifiedUtc,
    FileAttributes Attributes = FileAttributes.Normal)
{
    /// <summary>
    /// Lists the files under a path: a file gives one source named after it, and a
    /// directory gives every file below it, prefixed with the directory's own name.
    /// Symbolic links are skipped, so an archive cannot follow one out of the tree.
    /// </summary>
    /// <param name="path">A file or a directory on disk.</param>
    /// <returns>The sources, ready for <see cref="ArchiveWriter"/>.</returns>
    /// <exception cref="FileNotFoundException">The path does not exist.</exception>
    public static IReadOnlyList<ArchiveSource> FromPath(string path)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        if (File.Exists(fullPath))
            return [FromFile(fullPath, Path.GetFileName(fullPath))];

        if (!Directory.Exists(fullPath))
            throw new FileNotFoundException($"No such file or directory: {path}", path);

        string parent = Path.GetDirectoryName(fullPath) ?? fullPath;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
        };

        var sources = new List<ArchiveSource>();
        foreach (string file in Directory.EnumerateFiles(fullPath, "*", options))
            sources.Add(FromFile(file, Path.GetRelativePath(parent, file).Replace(Path.DirectorySeparatorChar, '/')));

        return sources;
    }

    private static ArchiveSource FromFile(string file, string archivePath) =>
        new(archivePath,
            () => new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read),
            File.GetLastWriteTimeUtc(file),
            File.GetAttributes(file));
}
