using System.Buffers.Binary;
using System.Text;

namespace CryptArc.Core.Sarc;

/// <summary>One row of a volume's file table, FORMAT_SPEC_P2 §2.2.</summary>
/// <param name="Name">Plain file name, no path.</param>
/// <param name="OriginalSize">Size of the whole file, not of its part in this volume.</param>
/// <param name="Crc32">CRC-32 of the whole file.</param>
/// <param name="FileType">Classifier group, 0…6.</param>
/// <param name="Strategy">Compression strategy chosen for the file.</param>
internal sealed record SarcFileRecord(string Name, uint OriginalSize, uint Crc32, SarcFileType FileType, byte Strategy);

/// <summary>One row of a volume's block table, §2.3, with where its data sits in the payload.</summary>
/// <param name="FileIndex">Index into this volume's file table.</param>
/// <param name="BlockSequence">Number of the block within its file, from 0.</param>
/// <param name="RawLength">Size of the block before coding.</param>
/// <param name="CodedLength">Size of the coded data.</param>
/// <param name="Flags">Bit 0 is <c>NO_COMPRESSION</c>; the others are zero.</param>
/// <param name="DataOffset">Offset of the coded data within the payload.</param>
internal readonly record struct SarcBlockRecord(
    ushort FileIndex, uint BlockSequence, uint RawLength, uint CodedLength, byte Flags, int DataOffset)
{
    /// <summary>The block is stored raw.</summary>
    public bool IsStored => (Flags & SarcPayload.NoCompression) != 0;
}

/// <summary>A parsed payload: the two tables and the payload they index into.</summary>
/// <param name="Files">The file table.</param>
/// <param name="Blocks">The block table.</param>
/// <param name="Bytes">The decrypted payload.</param>
internal sealed record SarcPayloadContents(SarcFileRecord[] Files, SarcBlockRecord[] Blocks, byte[] Bytes);

/// <summary>
/// The decrypted payload of a volume, FORMAT_SPEC_P2 §2:
/// <c>file_count u16, flags u8, reserved u8 = 0, file table, block_count u16, block table, block data</c>.
/// </summary>
internal static class SarcPayload
{
    /// <summary>The <c>NO_COMPRESSION</c> bit of <c>block_flags</c>.</summary>
    public const byte NoCompression = 0x01;

    /// <summary>The <c>PREDICTION</c> bit of <c>flags</c>, a team extension this reader does not implement.</summary>
    public const byte Prediction = 0x01;

    /// <summary>Bytes of fixed fields: file_count, flags, reserved, block_count.</summary>
    public const int FixedFieldsSize = 6;

    /// <summary>Size of one block table entry.</summary>
    public const int BlockRecordSize = 15;

    /// <summary>Highest valid <c>file_type</c>.</summary>
    public const byte MaxFileType = 6;

    private const int FileRecordFixedSize = 2 + 4 + 4 + 1 + 1;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Bytes a file table row takes.</summary>
    /// <param name="name">The file name.</param>
    /// <returns><c>2 + name_len + 10</c>.</returns>
    public static int FileRecordSize(string name) => FileRecordFixedSize + StrictUtf8.GetByteCount(name);

    /// <summary>
    /// Whether a stored name is a plain file name, §2.2 "Безпечні імена файлів": not empty,
    /// not <c>.</c> or <c>..</c>, no <c>/</c>, <c>\</c>, <c>:</c> or NUL, not absolute.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>Whether a reader may create a file with it.</returns>
    public static bool IsSafeName(string name) =>
        name.Length != 0
        && name is not "." and not ".."
        && name.AsSpan().IndexOfAny("/\\:\0") < 0
        && !Path.IsPathRooted(name);

    /// <summary>Serialises a payload.</summary>
    /// <param name="files">The file table.</param>
    /// <param name="blocks">The block table, data offsets ignored.</param>
    /// <param name="blockData">The coded data of each block, in table order.</param>
    /// <returns>The payload.</returns>
    public static byte[] Write(IReadOnlyList<SarcFileRecord> files, IReadOnlyList<SarcBlockRecord> blocks, IReadOnlyList<ReadOnlyMemory<byte>> blockData)
    {
        long size = FixedFieldsSize + (long)blocks.Count * BlockRecordSize;
        foreach (SarcFileRecord file in files)
            size += FileRecordSize(file.Name);
        foreach (ReadOnlyMemory<byte> data in blockData)
            size += data.Length;

        byte[] payload = new byte[checked((int)size)];
        Span<byte> span = payload;

        BinaryPrimitives.WriteUInt16BigEndian(span, checked((ushort)files.Count));
        int position = 4;

        foreach (SarcFileRecord file in files)
        {
            int nameLength = StrictUtf8.GetBytes(file.Name, span[(position + 2)..]);
            BinaryPrimitives.WriteUInt16BigEndian(span[position..], checked((ushort)nameLength));
            position += 2 + nameLength;
            BinaryPrimitives.WriteUInt32BigEndian(span[position..], file.OriginalSize);
            BinaryPrimitives.WriteUInt32BigEndian(span[(position + 4)..], file.Crc32);
            span[position + 8] = (byte)file.FileType;
            span[position + 9] = file.Strategy;
            position += 10;
        }

        BinaryPrimitives.WriteUInt16BigEndian(span[position..], checked((ushort)blocks.Count));
        position += 2;

        foreach (SarcBlockRecord block in blocks)
        {
            BinaryPrimitives.WriteUInt16BigEndian(span[position..], block.FileIndex);
            BinaryPrimitives.WriteUInt32BigEndian(span[(position + 2)..], block.BlockSequence);
            BinaryPrimitives.WriteUInt32BigEndian(span[(position + 6)..], block.RawLength);
            BinaryPrimitives.WriteUInt32BigEndian(span[(position + 10)..], block.CodedLength);
            span[position + 14] = block.Flags;
            position += BlockRecordSize;
        }

        foreach (ReadOnlyMemory<byte> data in blockData)
        {
            data.Span.CopyTo(span[position..]);
            position += data.Length;
        }

        return payload;
    }

    /// <summary>Step 7 of §4.3: parses the payload and checks every field the format constrains.</summary>
    /// <param name="payload">The decrypted payload.</param>
    /// <returns>The parsed tables.</returns>
    /// <exception cref="SarcException"><see cref="SarcErrorCode.Payload"/>.</exception>
    public static SarcPayloadContents Read(byte[] payload)
    {
        ReadOnlySpan<byte> span = payload;
        if (span.Length < 4)
            throw Fail("The payload is shorter than its fixed fields.");

        int fileCount = BinaryPrimitives.ReadUInt16BigEndian(span);
        byte flags = span[2];
        if (span[3] != 0)
            throw Fail($"reserved is {span[3]}, not 0.");

        if ((flags & Prediction) != 0)
            throw Fail("The volume uses the PREDICTION extension, which is not part of the base format.");

        if (flags != 0)
            throw Fail($"Unknown bits are set in flags: 0x{flags:X2}.");

        int position = 4;
        var files = new SarcFileRecord[fileCount];
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < fileCount; i++)
        {
            int nameLength = ReadUInt16(span, ref position, "file table");
            if (span.Length - position < nameLength + 10)
                throw Fail("The file table runs past the end of the payload.");

            string name;
            try
            {
                name = StrictUtf8.GetString(span.Slice(position, nameLength));
            }
            catch (DecoderFallbackException)
            {
                throw Fail($"The name of file {i} is not valid UTF-8.");
            }

            if (!IsSafeName(name))
                throw Fail($"Unsafe file name '{name}'.");

            if (!names.Add(name))
                throw Fail($"The name '{name}' appears twice in one file table.");

            position += nameLength;
            uint size = BinaryPrimitives.ReadUInt32BigEndian(span[position..]);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(span[(position + 4)..]);
            byte fileType = span[position + 8];
            byte strategy = span[position + 9];
            position += 10;

            if (fileType > MaxFileType)
                throw Fail($"file_type {fileType} of '{name}' is above {MaxFileType}.");

            files[i] = new SarcFileRecord(name, size, crc, (SarcFileType)fileType, strategy);
        }

        int blockCount = ReadUInt16(span, ref position, "block_count");
        if (span.Length - position < (long)blockCount * BlockRecordSize)
            throw Fail("The block table runs past the end of the payload.");

        var blocks = new SarcBlockRecord[blockCount];
        long dataOffset = position + (long)blockCount * BlockRecordSize;

        for (int i = 0; i < blockCount; i++)
        {
            ReadOnlySpan<byte> row = span.Slice(position + i * BlockRecordSize, BlockRecordSize);
            ushort fileIndex = BinaryPrimitives.ReadUInt16BigEndian(row);
            uint sequence = BinaryPrimitives.ReadUInt32BigEndian(row[2..]);
            uint rawLength = BinaryPrimitives.ReadUInt32BigEndian(row[6..]);
            uint codedLength = BinaryPrimitives.ReadUInt32BigEndian(row[10..]);
            byte blockFlags = row[14];

            if (fileIndex >= fileCount)
                throw Fail($"Block {i} names file {fileIndex}; the table has {fileCount}.");

            if ((blockFlags & ~NoCompression) != 0)
                throw Fail($"Block {i} has unknown block_flags bits: 0x{blockFlags:X2}.");

            bool stored = (blockFlags & NoCompression) != 0;
            if (stored && codedLength != rawLength)
                throw Fail($"Block {i} is stored, but coded_len {codedLength} differs from raw_len {rawLength}.");

            if (rawLength > files[fileIndex].OriginalSize)
                throw Fail($"Block {i} is larger than its whole file.");

            if (dataOffset + codedLength > span.Length)
                throw Fail($"The data of block {i} runs past the end of the payload.");

            blocks[i] = new SarcBlockRecord(fileIndex, sequence, rawLength, codedLength, blockFlags, (int)dataOffset);
            dataOffset += codedLength;
        }

        if (dataOffset != span.Length)
            throw Fail($"{span.Length - dataOffset} bytes follow the last block.");

        return new SarcPayloadContents(files, blocks, payload);
    }

    private static int ReadUInt16(ReadOnlySpan<byte> span, ref int position, string what)
    {
        if (span.Length - position < 2)
            throw Fail($"The payload ends inside the {what}.");

        int value = BinaryPrimitives.ReadUInt16BigEndian(span[position..]);
        position += 2;
        return value;
    }

    private static SarcException Fail(string message) => new(SarcErrorCode.Payload, message);
}
