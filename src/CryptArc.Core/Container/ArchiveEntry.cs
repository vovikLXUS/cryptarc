namespace CryptArc.Core.Container;

/// <summary>One file stored in an archive.</summary>
public sealed class ArchiveEntry
{
    internal ArchiveEntry(
        int index, string path, long originalSize, long storedSize,
        uint firstBlock, uint blockCount, long modifiedUtcTicks, uint attributes, byte[] sha256)
    {
        Index = index;
        Path = path;
        OriginalSize = originalSize;
        StoredSize = storedSize;
        FirstBlock = firstBlock;
        BlockCount = blockCount;
        ModifiedUtcTicks = modifiedUtcTicks;
        Attributes = attributes;
        Sha256 = sha256;
    }

    internal int Index { get; }

    internal uint FirstBlock { get; }

    internal uint BlockCount { get; }

    internal long ModifiedUtcTicks { get; }

    internal uint Attributes { get; }

    internal byte[] Sha256 { get; }

    /// <summary>Relative path inside the archive, separated by '/'.</summary>
    public string Path { get; }

    /// <summary>Size of the original contents in bytes.</summary>
    public long OriginalSize { get; }

    /// <summary>Compressed size in bytes, without the per-block nonce and tag.</summary>
    public long StoredSize { get; }

    /// <summary>Last modification time, in UTC.</summary>
    public DateTime ModifiedUtc => new(ModifiedUtcTicks, DateTimeKind.Utc);
}
