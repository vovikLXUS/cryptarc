using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CryptArc.Core.Blocks;

namespace CryptArc.Core.Container;

/// <summary>One row of the block index.</summary>
internal readonly record struct BlockRecord(long Offset, uint StoredSize, uint OriginalSize, CodecId Codec, uint Crc32);

/// <summary>The plaintext header, as specified in docs/container-format.md.</summary>
internal readonly record struct ArchiveHeader(
    byte[] Salt, uint KdfIterations, uint KdfMemoryKiB, uint BlockSize, uint EntryCount, long TableOffset, long TableLength);

/// <summary>Constants and the byte-level layout of a <c>.cryptarc</c> file.</summary>
internal static class ArchiveFormat
{
    public const int HeaderSize = 64;
    public const int SaltSize = 16;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int MacSize = 32;
    public const int BlockOverhead = NonceSize + TagSize;

    public const ushort CurrentVersion = 1;
    public const ushort FlagEncrypted = 1;
    public const byte KdfArgon2id = 2;
    public const byte CipherAes256Gcm = 1;

    public const uint MinKdfIterations = 1;
    public const uint MaxKdfIterations = 64;
    public const uint MinKdfMemoryKiB = 32;
    public const uint MaxKdfMemoryKiB = 2 * 1024 * 1024;

    /// <summary>Associated data that binds the encrypted table to its role; no block has this index.</summary>
    public const uint TableAad = uint.MaxValue;

    private const int BlockRecordSize = 21;
    private const int TablePrefixSize = 5;
    private const int MinTableLength = BlockOverhead + TablePrefixSize;
    private const int MacBufferSize = 1024 * 1024;

    private static readonly byte[] Magic = "CRYPTARC"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void WriteHeader(Span<byte> destination, in ArchiveHeader header)
    {
        destination[..HeaderSize].Clear();
        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], CurrentVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[10..], FlagEncrypted);
        destination[12] = KdfArgon2id;
        destination[13] = CipherAes256Gcm;
        header.Salt.CopyTo(destination[16..]);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[32..], header.KdfIterations);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[36..], header.KdfMemoryKiB);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[40..], header.BlockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[44..], header.EntryCount);
        BinaryPrimitives.WriteInt64LittleEndian(destination[48..], header.TableOffset);
        BinaryPrimitives.WriteInt64LittleEndian(destination[56..], header.TableLength);
    }

    /// <summary>
    /// Parses the header. It is not authenticated yet at this point, so every field that
    /// drives work or an allocation is range-checked first: without the limits, a crafted
    /// file could make the reader run Argon2 over terabytes of memory.
    /// </summary>
    public static ArchiveHeader ReadHeader(ReadOnlySpan<byte> source, long archiveLength)
    {
        if (!source[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("Not a .cryptarc archive.");

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(source[8..]);
        if (version != CurrentVersion)
            throw new NotSupportedException($"Unsupported archive format version {version}.");

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(source[10..]);
        if (flags != FlagEncrypted)
            throw new NotSupportedException($"Unsupported archive flags 0x{flags:X4}.");

        if (source[12] != KdfArgon2id)
            throw new NotSupportedException($"Unsupported key derivation function {source[12]}.");

        if (source[13] != CipherAes256Gcm)
            throw new NotSupportedException($"Unsupported cipher {source[13]}.");

        if (BinaryPrimitives.ReadUInt16LittleEndian(source[14..]) != 0)
            throw new InvalidDataException("The reserved header field is not zero.");

        uint iterations = BinaryPrimitives.ReadUInt32LittleEndian(source[32..]);
        uint memoryKiB = BinaryPrimitives.ReadUInt32LittleEndian(source[36..]);
        if (iterations is < MinKdfIterations or > MaxKdfIterations || memoryKiB is < MinKdfMemoryKiB or > MaxKdfMemoryKiB)
            throw new InvalidDataException("The key derivation parameters are out of range.");

        uint blockSize = BinaryPrimitives.ReadUInt32LittleEndian(source[40..]);
        if (blockSize is < BlockCompressor.MinBlockSize or > BlockCompressor.MaxBlockSize)
            throw new InvalidDataException("The block size is out of range.");

        long tableOffset = BinaryPrimitives.ReadInt64LittleEndian(source[48..]);
        long tableLength = BinaryPrimitives.ReadInt64LittleEndian(source[56..]);
        long tableEnd = archiveLength - MacSize;
        if (tableOffset < HeaderSize || tableLength < MinTableLength
            || tableOffset > tableEnd || tableLength != tableEnd - tableOffset)
            throw new InvalidDataException("The archive is truncated or its table location is invalid.");

        return new ArchiveHeader(
            source.Slice(16, SaltSize).ToArray(), iterations, memoryKiB, blockSize,
            BinaryPrimitives.ReadUInt32LittleEndian(source[44..]), tableOffset, tableLength);
    }

    /// <summary>HMAC-SHA256 over the first <paramref name="length"/> bytes of the stream.</summary>
    public static void ComputeMac(Stream stream, long length, byte[] macKey, Span<byte> destination, CancellationToken cancellationToken)
    {
        using IncrementalHash hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, macKey);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MacBufferSize);
        try
        {
            stream.Position = 0;
            for (long remaining = length; remaining > 0;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = stream.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                if (read == 0)
                    throw new InvalidDataException("The archive is truncated.");

                hmac.AppendData(buffer, 0, read);
                remaining -= read;
            }

            hmac.GetHashAndReset(destination);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static void WriteAad(Span<byte> destination, uint index) =>
        BinaryPrimitives.WriteUInt32LittleEndian(destination, index);

    /// <summary>
    /// Whether a stored path is a plain relative one. A path comes out of the file and
    /// becomes a location on disk, so this is what stops an archive from writing outside
    /// the extraction directory.
    /// </summary>
    public static bool IsSafePath(string path)
    {
        if (path.Length == 0 || path.AsSpan().IndexOfAny('\\', ':', '\0') >= 0)
            return false;

        foreach (Range range in path.AsSpan().Split('/'))
        {
            ReadOnlySpan<char> segment = path.AsSpan()[range];
            if (segment.IsEmpty || segment is "." or "..")
                return false;
        }

        return true;
    }

    public static byte[] SerializeTable(IReadOnlyList<ArchiveEntry> entries, IReadOnlyList<BlockRecord> blocks)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, StrictUtf8);

        foreach (ArchiveEntry entry in entries)
        {
            byte[] path = StrictUtf8.GetBytes(entry.Path);
            writer.Write(checked((ushort)path.Length));
            writer.Write(path);
            writer.Write(entry.OriginalSize);
            writer.Write(entry.StoredSize);
            writer.Write(entry.FirstBlock);
            writer.Write(entry.BlockCount);
            writer.Write(entry.ModifiedUtcTicks);
            writer.Write(entry.Attributes);
            writer.Write(entry.Sha256);
        }

        writer.Write((uint)blocks.Count);
        foreach (BlockRecord block in blocks)
        {
            writer.Write(block.Offset);
            writer.Write(block.StoredSize);
            writer.Write(block.OriginalSize);
            writer.Write((byte)block.Codec);
            writer.Write(block.Crc32);
        }

        writer.Flush();
        return buffer.ToArray();
    }

    /// <summary>
    /// Parses the decrypted table. Entries must partition the block index exactly, in
    /// order, which also keeps the size checks linear in the number of blocks.
    /// </summary>
    public static (ArchiveEntry[] Entries, BlockRecord[] Blocks) ParseTable(byte[] table, in ArchiveHeader header)
    {
        try
        {
            using var buffer = new MemoryStream(table, writable: false);
            using var reader = new BinaryReader(buffer, StrictUtf8);

            var parsed = new List<ArchiveEntry>();
            for (uint i = 0; i < header.EntryCount; i++)
                parsed.Add(ReadEntry(reader, parsed.Count));

            // u32 in the specification; a count beyond int.MaxValue could not fit in memory anyway.
            uint blockCount = reader.ReadUInt32();
            if (blockCount > int.MaxValue || blockCount * (long)BlockRecordSize != buffer.Length - buffer.Position)
                throw new InvalidDataException("The block index has the wrong length.");

            var blocks = new BlockRecord[blockCount];
            for (int i = 0; i < blocks.Length; i++)
                blocks[i] = ReadBlock(reader, header);

            CheckPartition(parsed, blocks);
            return (parsed.ToArray(), blocks);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("The file table is truncated.", exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("A stored path is not valid UTF-8.", exception);
        }
    }

    private static ArchiveEntry ReadEntry(BinaryReader reader, int index)
    {
        ushort pathLength = reader.ReadUInt16();
        byte[] pathBytes = reader.ReadBytes(pathLength);
        if (pathBytes.Length != pathLength)
            throw new EndOfStreamException();

        string path = StrictUtf8.GetString(pathBytes);
        if (!IsSafePath(path))
            throw new InvalidDataException($"Unsafe path in the archive: '{path}'.");

        long originalSize = reader.ReadInt64();
        long storedSize = reader.ReadInt64();
        uint firstBlock = reader.ReadUInt32();
        uint blockCount = reader.ReadUInt32();
        long modifiedTicks = reader.ReadInt64();
        uint attributes = reader.ReadUInt32();
        byte[] sha256 = reader.ReadBytes(SHA256.HashSizeInBytes);
        if (sha256.Length != SHA256.HashSizeInBytes)
            throw new EndOfStreamException();

        if (originalSize < 0 || storedSize < 0 || modifiedTicks < 0 || modifiedTicks > DateTime.MaxValue.Ticks)
            throw new InvalidDataException($"Invalid metadata for '{path}'.");

        return new ArchiveEntry(index, path, originalSize, storedSize, firstBlock, blockCount, modifiedTicks, attributes, sha256);
    }

    private static BlockRecord ReadBlock(BinaryReader reader, in ArchiveHeader header)
    {
        long offset = reader.ReadInt64();
        uint storedSize = reader.ReadUInt32();
        uint originalSize = reader.ReadUInt32();
        var codec = (CodecId)reader.ReadByte();
        uint crc = reader.ReadUInt32();

        if (originalSize == 0 || originalSize > header.BlockSize || storedSize > originalSize
            || offset < HeaderSize || offset > header.TableOffset - BlockOverhead - storedSize)
            throw new InvalidDataException("A block index entry is out of range.");

        return new BlockRecord(offset, storedSize, originalSize, codec, crc);
    }

    private static void CheckPartition(List<ArchiveEntry> entries, BlockRecord[] blocks)
    {
        long next = 0;
        foreach (ArchiveEntry entry in entries)
        {
            if (entry.FirstBlock != next || next + entry.BlockCount > blocks.Length)
                throw new InvalidDataException($"The block range of '{entry.Path}' is invalid.");

            long original = 0, stored = 0;
            for (long i = next; i < next + entry.BlockCount; i++)
            {
                original += blocks[i].OriginalSize;
                stored += blocks[i].StoredSize;
            }

            if (original != entry.OriginalSize || stored != entry.StoredSize)
                throw new InvalidDataException($"The sizes of '{entry.Path}' disagree with its blocks.");

            next += entry.BlockCount;
        }

        if (next != blocks.Length)
            throw new InvalidDataException("The block index has entries that belong to no file.");
    }
}
