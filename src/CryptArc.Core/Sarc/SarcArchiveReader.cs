using CryptArc.Core.Hashing;

namespace CryptArc.Core.Sarc;

/// <summary>A file restored from a SARC archive.</summary>
/// <param name="Name">The stored name.</param>
/// <param name="Data">The contents, CRC-checked.</param>
/// <param name="FileType">The recorded classifier group.</param>
/// <param name="Strategy">The recorded strategy byte.</param>
public sealed record SarcFile(string Name, byte[] Data, SarcFileType FileType, byte Strategy);

/// <summary>
/// Reads SARC v1 archives, following the order of FORMAT_SPEC_P2 §4.3 and reporting every
/// rejection as a <see cref="SarcException"/> with the specification's code.
/// </summary>
/// <remarks>
/// <para>Order, per volume and then for the set:</para>
/// <list type="number">
/// <item>length ≥ 73 (<c>E_SHORT</c>), magic and version (<c>E_MAGIC</c>), length against <c>ciphertext_len</c> (<c>E_LENGTH</c>);</item>
/// <item>the same salt in every volume, else they come from different archives (<c>E_VOLUMES</c>);</item>
/// <item>PBKDF2 once; the HMAC in constant time before any decryption (<c>E_HMAC</c>);</item>
/// <item>decryption, then the payload's fields (<c>E_PAYLOAD</c>) and every block (<c>E_CODE</c>, <c>E_PAYLOAD</c>);</item>
/// <item>the set: as many volumes as <c>volume_count</c>, no index twice or out of range, one <c>volume_count</c> (<c>E_VOLUMES</c>);</item>
/// <item>each file's blocks joined by (name, <c>block_seq</c>): a gap-free series from 0 summing to <c>orig_size</c> (<c>E_PAYLOAD</c>);</item>
/// <item>the CRC-32 of each file (<c>E_CRC</c>).</item>
/// </list>
/// <para>
/// Volumes may be given in any order. Everything is decoded in memory before a single
/// file is written, so a damaged archive leaves nothing behind.
/// </para>
/// </remarks>
public static class SarcArchiveReader
{
    /// <summary>
    /// Upper bound on <c>raw_len / coded_len</c>: the cheapest element, a 258-byte reference
    /// with 1-bit codes, costs 3 bits. A larger declared ratio cannot be genuine and is
    /// refused before the output buffer is allocated.
    /// </summary>
    private const long MaxExpansion = 8L * SarcBlockCodec.MaxMatch / 3 + 1;

    /// <summary>Reads volume files from disk, in any order.</summary>
    /// <param name="paths">Every volume of the archive.</param>
    /// <param name="password">The password.</param>
    /// <returns>The files, in the order the archive lists them.</returns>
    /// <exception cref="SarcException">The archive is rejected.</exception>
    public static IReadOnlyList<SarcFile> ReadFiles(IEnumerable<string> paths, string password)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var volumes = new List<ReadOnlyMemory<byte>>();
        foreach (string path in paths)
            volumes.Add(File.ReadAllBytes(path));

        return Read(volumes, password);
    }

    /// <summary>Reads the volumes of an archive, in any order.</summary>
    /// <param name="volumes">Every volume of the archive.</param>
    /// <param name="password">The password.</param>
    /// <returns>The files, in the order the archive lists them.</returns>
    /// <exception cref="SarcException">The archive is rejected.</exception>
    public static IReadOnlyList<SarcFile> Read(IReadOnlyList<ReadOnlyMemory<byte>> volumes, string password)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(password);

        if (volumes.Count == 0)
            throw new SarcException(SarcErrorCode.Volumes, "No volumes were given.");

        var headers = new SarcVolumeHeader[volumes.Count];
        for (int v = 0; v < volumes.Count; v++)
            headers[v] = SarcVolume.ReadHeader(volumes[v].Span);

        for (int v = 1; v < headers.Length; v++)
        {
            if (!headers[v].Salt.AsSpan().SequenceEqual(headers[0].Salt))
                throw new SarcException(SarcErrorCode.Volumes, "The volumes carry different salts: they belong to different archives.");
        }

        var payloads = new byte[volumes.Count][];
        using (SarcKeys keys = SarcKeys.Derive(password, headers[0].Salt))
        {
            for (int v = 0; v < volumes.Count; v++)
                payloads[v] = SarcVolume.Open(volumes[v].Span, headers[v], keys);
        }

        return ReadDecrypted(headers, payloads);
    }

    /// <summary>Everything after decryption: payloads, blocks, the volume set, the files.</summary>
    /// <param name="headers">The volumes' headers, in the order given.</param>
    /// <param name="payloads">Their authenticated, decrypted payloads, in the same order.</param>
    /// <returns>The files.</returns>
    internal static IReadOnlyList<SarcFile> ReadDecrypted(SarcVolumeHeader[] headers, byte[][] payloads)
    {
        var contents = new SarcPayloadContents[payloads.Length];
        var decoded = new byte[payloads.Length][][];

        for (int v = 0; v < payloads.Length; v++)
            contents[v] = SarcPayload.Read(payloads[v]);

        for (int v = 0; v < payloads.Length; v++)
            decoded[v] = DecodeBlocks(contents[v]);

        CheckVolumeSet(headers);

        int[] order = new int[payloads.Length];
        for (int v = 0; v < order.Length; v++)
            order[v] = v;
        Array.Sort(order, (a, b) => headers[a].VolumeIndex.CompareTo(headers[b].VolumeIndex));

        return Assemble(order, contents, decoded);
    }

    /// <summary>
    /// Writes restored files into a directory, created if missing. Nothing is written if
    /// any of the names already exists there.
    /// </summary>
    /// <param name="files">Files from <see cref="Read"/>.</param>
    /// <param name="directory">The destination.</param>
    /// <exception cref="IOException">A file of that name already exists.</exception>
    public static void ExtractTo(IReadOnlyList<SarcFile> files, string directory)
    {
        ArgumentNullException.ThrowIfNull(files);
        string root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);

        var targets = new string[files.Count];
        for (int i = 0; i < files.Count; i++)
        {
            // Names were checked on reading; checked again because this is where a bad one does harm.
            if (!SarcPayload.IsSafeName(files[i].Name))
                throw new SarcException(SarcErrorCode.Payload, $"Unsafe file name '{files[i].Name}'.");

            targets[i] = Path.Combine(root, files[i].Name);
            if (File.Exists(targets[i]) || Directory.Exists(targets[i]))
                throw new IOException($"The file already exists: {targets[i]}");
        }

        for (int i = 0; i < files.Count; i++)
        {
            string temporary = $"{targets[i]}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(temporary, files[i].Data);
                File.Move(temporary, targets[i], overwrite: false);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }

    private static byte[][] DecodeBlocks(SarcPayloadContents contents)
    {
        var output = new byte[contents.Blocks.Length][];
        for (int i = 0; i < contents.Blocks.Length; i++)
        {
            SarcBlockRecord block = contents.Blocks[i];
            ReadOnlySpan<byte> coded = contents.Bytes.AsSpan(block.DataOffset, (int)block.CodedLength);

            if (block.IsStored)
            {
                output[i] = coded.ToArray();
                continue;
            }

            if (block.RawLength > Array.MaxLength || block.RawLength > MaxExpansion * Math.Max(1, coded.Length))
                throw new SarcException(SarcErrorCode.Payload, $"Block {i} declares raw_len {block.RawLength} for {coded.Length} coded bytes.");

            output[i] = new byte[block.RawLength];
            SarcBlockCodec.Decode(coded, output[i]);
        }

        return output;
    }

    private static void CheckVolumeSet(SarcVolumeHeader[] headers)
    {
        int count = headers[0].VolumeCount;
        var seen = new bool[count];

        foreach (SarcVolumeHeader header in headers)
        {
            if (header.VolumeCount != count)
                throw new SarcException(SarcErrorCode.Volumes, $"One volume declares {count} volumes, another {header.VolumeCount}.");

            if (header.VolumeIndex >= count)
                throw new SarcException(SarcErrorCode.Volumes, $"volume_index {header.VolumeIndex} is not below volume_count {count}.");

            if (seen[header.VolumeIndex])
                throw new SarcException(SarcErrorCode.Volumes, $"Volume {header.VolumeIndex} was given twice.");

            seen[header.VolumeIndex] = true;
        }

        if (headers.Length != count)
            throw new SarcException(SarcErrorCode.Volumes, $"{headers.Length} of {count} volumes were given.");
    }

    private static List<SarcFile> Assemble(int[] order, SarcPayloadContents[] contents, byte[][][] decoded)
    {
        var assemblies = new Dictionary<string, (SarcFileRecord Record, SortedDictionary<uint, byte[]> Blocks)>(StringComparer.Ordinal);
        var names = new List<string>();

        foreach (int v in order)
        {
            SarcPayloadContents volume = contents[v];
            foreach (SarcFileRecord file in volume.Files)
            {
                if (assemblies.TryGetValue(file.Name, out var known))
                {
                    if (known.Record.OriginalSize != file.OriginalSize || known.Record.Crc32 != file.Crc32)
                        throw new SarcException(SarcErrorCode.Payload, $"The volumes disagree about the size or CRC-32 of '{file.Name}'.");
                }
                else
                {
                    assemblies[file.Name] = (file, new SortedDictionary<uint, byte[]>());
                    names.Add(file.Name);
                }
            }

            for (int i = 0; i < volume.Blocks.Length; i++)
            {
                SarcBlockRecord block = volume.Blocks[i];
                string name = volume.Files[block.FileIndex].Name;
                if (!assemblies[name].Blocks.TryAdd(block.BlockSequence, decoded[v][i]))
                    throw new SarcException(SarcErrorCode.Payload, $"Block {block.BlockSequence} of '{name}' appears twice.");
            }
        }

        var files = new List<SarcFile>(names.Count);
        foreach (string name in names)
        {
            (SarcFileRecord record, SortedDictionary<uint, byte[]> blocks) = assemblies[name];

            long total = 0;
            uint expected = 0;
            foreach ((uint sequence, byte[] data) in blocks)
            {
                if (sequence != expected)
                    throw new SarcException(SarcErrorCode.Payload, $"Block {expected} of '{name}' is missing.");

                expected++;
                total += data.Length;
            }

            if (total != record.OriginalSize)
                throw new SarcException(SarcErrorCode.Payload, $"The blocks of '{name}' hold {total} bytes; orig_size is {record.OriginalSize}.");

            if (total > Array.MaxLength)
                throw new NotSupportedException($"'{name}' is larger than this reader can hold in memory.");

            byte[] content = new byte[total];
            int offset = 0;
            foreach (byte[] data in blocks.Values)
            {
                data.CopyTo(content, offset);
                offset += data.Length;
            }

            if (Crc32.Compute(content) != record.Crc32)
                throw new SarcException(SarcErrorCode.Crc, $"The CRC-32 of '{name}' does not match the recorded {record.Crc32:X8}.");

            files.Add(new SarcFile(name, content, record.FileType, record.Strategy));
        }

        return files;
    }
}
