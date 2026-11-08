using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using CryptArc.Core.Hashing;

namespace CryptArc.Core.Container;

/// <summary>
/// Opens encrypted <c>.cryptarc</c> archives. Opening checks the trailer HMAC over the
/// whole file before anything else in it is trusted; a wrong password and a tampered
/// archive are indistinguishable and both raise <see cref="CryptographicException"/>.
/// </summary>
/// <remarks>Not thread-safe: all operations share one stream position.</remarks>
public sealed class ArchiveReader : IDisposable
{
    private const FileAttributes RestoredAttributes = FileAttributes.ReadOnly | FileAttributes.Hidden;

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly ArchiveKeys _keys;
    private readonly ArchiveEntry[] _entries;
    private readonly BlockRecord[] _blocks;

    private ArchiveReader(Stream stream, bool ownsStream, ArchiveKeys keys, ArchiveEntry[] entries, BlockRecord[] blocks)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        _keys = keys;
        _entries = entries;
        _blocks = blocks;
    }

    /// <summary>The files in the archive, in archive order.</summary>
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    /// <summary>Opens an archive file.</summary>
    /// <param name="path">The archive.</param>
    /// <param name="password">The password.</param>
    /// <param name="cancellationToken">Stops the integrity check.</param>
    /// <returns>A reader that owns the file handle.</returns>
    /// <inheritdoc cref="Open(Stream, string, CancellationToken)" path="/exception"/>
    public static ArchiveReader OpenFile(string path, string password, CancellationToken cancellationToken = default)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            return Open(stream, password, ownsStream: true, cancellationToken);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Opens an archive from a stream, which is left open.</summary>
    /// <param name="stream">A seekable, readable stream.</param>
    /// <param name="password">The password.</param>
    /// <param name="cancellationToken">Stops the integrity check.</param>
    /// <returns>A reader over the stream.</returns>
    /// <exception cref="InvalidDataException">The file is not an archive, is truncated or is malformed.</exception>
    /// <exception cref="NotSupportedException">The format version, cipher or KDF is not supported.</exception>
    /// <exception cref="CryptographicException">The password is wrong or the archive was altered.</exception>
    public static ArchiveReader Open(Stream stream, string password, CancellationToken cancellationToken = default) =>
        Open(stream, password, ownsStream: false, cancellationToken);

    private static ArchiveReader Open(Stream stream, string password, bool ownsStream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(password);

        if (!stream.CanSeek || !stream.CanRead)
            throw new ArgumentException("The stream must be seekable and readable.", nameof(stream));

        long length = stream.Length;
        if (length < ArchiveFormat.HeaderSize + ArchiveFormat.MacSize)
            throw new InvalidDataException("The file is too short to be an archive.");

        Span<byte> headerBytes = stackalloc byte[ArchiveFormat.HeaderSize];
        stream.Position = 0;
        stream.ReadExactly(headerBytes);
        ArchiveHeader header = ArchiveFormat.ReadHeader(headerBytes, length);

        ArchiveKeys keys = ArchiveKeys.Derive(password, header.Salt, header.KdfIterations, header.KdfMemoryKiB);
        try
        {
            VerifyMac(stream, length, keys.MacKey, cancellationToken);

            byte[] table = ReadTable(stream, header, keys.ContentKey);
            (ArchiveEntry[] entries, BlockRecord[] blocks) = ArchiveFormat.ParseTable(table, header);
            return new ArchiveReader(stream, ownsStream, keys, entries, blocks);
        }
        catch
        {
            keys.Dispose();
            throw;
        }
    }

    /// <summary>Restores one file into a stream.</summary>
    /// <param name="entry">An entry of this archive.</param>
    /// <param name="destination">Receives the original contents.</param>
    /// <param name="cancellationToken">Stops the run between blocks.</param>
    /// <exception cref="ArgumentException">The entry belongs to another archive.</exception>
    /// <exception cref="InvalidDataException">A block or the file fails its checksum.</exception>
    /// <exception cref="NotSupportedException">A block uses a codec this build does not have.</exception>
    /// <exception cref="CryptographicException">A block fails authentication.</exception>
    public void Extract(ArchiveEntry entry, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(destination);

        if (entry.Index >= _entries.Length || !ReferenceEquals(_entries[entry.Index], entry))
            throw new ArgumentException("The entry does not belong to this archive.", nameof(entry));

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        for (uint index = entry.FirstBlock; index < entry.FirstBlock + entry.BlockCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            BlockRecord block = _blocks[index];
            byte[] sealedBlock = ArrayPool<byte>.Shared.Rent(checked((int)block.StoredSize) + ArchiveFormat.BlockOverhead);
            byte[] plain = ArrayPool<byte>.Shared.Rent((int)block.OriginalSize);
            try
            {
                Span<byte> restored = plain.AsSpan(0, (int)block.OriginalSize);
                OpenBlock(block, index, sealedBlock, restored);

                hash.AppendData(restored);
                destination.Write(restored);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(sealedBlock, clearArray: true);
                ArrayPool<byte>.Shared.Return(plain, clearArray: true);
            }
        }

        Span<byte> actual = stackalloc byte[SHA256.HashSizeInBytes];
        hash.GetHashAndReset(actual);
        if (!actual.SequenceEqual(entry.Sha256))
            throw new InvalidDataException($"The contents of '{entry.Path}' fail their SHA-256 check.");
    }

    /// <summary>
    /// Restores every file below a directory. A file appears only once it has passed all
    /// of its checks, and an existing file is never replaced.
    /// </summary>
    /// <param name="directory">The extraction root; created if missing.</param>
    /// <param name="cancellationToken">Stops the run between blocks.</param>
    /// <exception cref="IOException">A file to be restored already exists.</exception>
    /// <exception cref="InvalidDataException">A stored path leaves the directory, or a checksum fails.</exception>
    /// <exception cref="CryptographicException">A block fails authentication.</exception>
    public void ExtractAll(string directory, CancellationToken cancellationToken = default)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;

        foreach (ArchiveEntry entry in _entries)
        {
            string target = Path.GetFullPath(Path.Combine(root, entry.Path));
            if (!target.StartsWith(root, StringComparison.Ordinal))
                throw new InvalidDataException($"Unsafe path in the archive: '{entry.Path}'.");

            if (File.Exists(target))
                throw new IOException($"The file already exists: {target}");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            string temporary = $"{target}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    Extract(entry, output, cancellationToken);

                File.Move(temporary, target, overwrite: false);
            }
            finally
            {
                File.Delete(temporary);
            }

            File.SetLastWriteTimeUtc(target, entry.ModifiedUtc);
            var attributes = (FileAttributes)entry.Attributes & RestoredAttributes;
            if (attributes != 0)
                File.SetAttributes(target, attributes);
        }
    }

    /// <summary>
    /// Decrypts, decompresses and checks every file without writing anything. The HMAC
    /// was already checked on opening; this also proves each block decodes correctly.
    /// </summary>
    /// <param name="cancellationToken">Stops the run between blocks.</param>
    /// <exception cref="InvalidDataException">A block or a file fails its checksum.</exception>
    /// <exception cref="CryptographicException">A block fails authentication.</exception>
    public void Verify(CancellationToken cancellationToken = default)
    {
        foreach (ArchiveEntry entry in _entries)
            Extract(entry, Stream.Null, cancellationToken);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _keys.Dispose();
        if (_ownsStream)
            _stream.Dispose();
    }

    private void OpenBlock(in BlockRecord block, uint index, byte[] sealedBlock, Span<byte> restored)
    {
        int storedSize = (int)block.StoredSize;
        int sealedSize = storedSize + ArchiveFormat.BlockOverhead;

        _stream.Position = block.Offset;
        _stream.ReadExactly(sealedBlock, 0, sealedSize);

        Span<byte> aad = stackalloc byte[sizeof(uint)];
        ArchiveFormat.WriteAad(aad, index);

        Span<byte> payload = sealedBlock.AsSpan(ArchiveFormat.NonceSize, storedSize);
        using (var aes = new AesGcm(_keys.ContentKey, ArchiveFormat.TagSize))
        {
            aes.Decrypt(
                sealedBlock.AsSpan(0, ArchiveFormat.NonceSize), payload,
                sealedBlock.AsSpan(ArchiveFormat.NonceSize + storedSize, ArchiveFormat.TagSize), payload, aad);
        }

        if (CodecRegistry.Get(block.Codec).Decode(payload, restored) != restored.Length)
            throw new InvalidDataException($"Block {index} decoded to the wrong length.");

        if (Crc32.Compute(restored) != block.Crc32)
            throw new InvalidDataException($"Block {index} fails its CRC-32 check.");
    }

    private static void VerifyMac(Stream stream, long length, byte[] macKey, CancellationToken cancellationToken)
    {
        long macOffset = length - ArchiveFormat.MacSize;

        Span<byte> actual = stackalloc byte[ArchiveFormat.MacSize];
        ArchiveFormat.ComputeMac(stream, macOffset, macKey, actual, cancellationToken);

        Span<byte> expected = stackalloc byte[ArchiveFormat.MacSize];
        stream.Position = macOffset;
        stream.ReadExactly(expected);

        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new CryptographicException("Wrong password, or the archive has been altered.");
    }

    private static byte[] ReadTable(Stream stream, in ArchiveHeader header, byte[] contentKey)
    {
        const int PrefixSize = 5;

        if (header.TableLength > int.MaxValue)
            throw new InvalidDataException("The file table is too large.");

        byte[] sealedTable = new byte[header.TableLength];
        stream.Position = header.TableOffset;
        stream.ReadExactly(sealedTable);

        int payloadLength = sealedTable.Length - ArchiveFormat.BlockOverhead;
        Span<byte> payload = sealedTable.AsSpan(ArchiveFormat.NonceSize, payloadLength);

        Span<byte> aad = stackalloc byte[sizeof(uint)];
        ArchiveFormat.WriteAad(aad, ArchiveFormat.TableAad);

        using (var aes = new AesGcm(contentKey, ArchiveFormat.TagSize))
        {
            aes.Decrypt(
                sealedTable.AsSpan(0, ArchiveFormat.NonceSize), payload,
                sealedTable.AsSpan(ArchiveFormat.NonceSize + payloadLength, ArchiveFormat.TagSize), payload, aad);
        }

        var codec = (CodecId)payload[0];
        int originalSize = BinaryPrimitives.ReadInt32LittleEndian(payload[1..]);
        if (originalSize < 0)
            throw new InvalidDataException("The file table has a negative size.");

        byte[] table = new byte[originalSize];
        if (CodecRegistry.Get(codec).Decode(payload[PrefixSize..], table) != originalSize)
            throw new InvalidDataException("The file table decoded to the wrong length.");

        return table;
    }
}
