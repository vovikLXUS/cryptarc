using System.Buffers.Binary;
using System.Security.Cryptography;
using CryptArc.Core.Hashing;

namespace CryptArc.Core.Sarc;

/// <summary>
/// Creates SARC v1 archives as FORMAT_SPEC_P2 defines them: files cut into blocks, each
/// block LZ77 + Huffman coded or stored raw, blocks spread over volumes by a knapsack, each
/// volume encrypted with AES-256-CTR and authenticated with HMAC-SHA256.
/// </summary>
/// <remarks>
/// <para>Order of operations: classify → cut into blocks → code → pack → encrypt → MAC.</para>
/// <para>
/// <c>strategy</c> values written by this implementation: 0 — every block stored raw
/// (already-compressed data), 1 — LZ77 + Huffman with lazy matching, 2 — LZ77 + Huffman
/// with greedy matching. Any single block that does not shrink is stored raw regardless,
/// with <c>NO_COMPRESSION</c> set and <c>coded_len = raw_len</c>.
/// </para>
/// <para>
/// PBKDF2 runs once per archive. Every archive gets a fresh random salt and nonce prefix;
/// volume <c>k</c> uses <c>nonce = prefix(8) ‖ uint32_BE(k)</c>.
/// </para>
/// </remarks>
public static class SarcArchiveWriter
{
    /// <summary>Strategy byte: blocks stored raw.</summary>
    public const byte StrategyStored = 0;

    /// <summary>Strategy byte: LZ77 with lazy matching, then Huffman.</summary>
    public const byte StrategyLazy = 1;

    /// <summary>Strategy byte: LZ77 with greedy matching, then Huffman.</summary>
    public const byte StrategyGreedy = 2;

    private const int NoncePrefixSize = 8;

    /// <summary>Builds the volumes of an archive in memory.</summary>
    /// <param name="sources">The files; names must be valid and distinct.</param>
    /// <param name="password">The password; not empty.</param>
    /// <param name="options">Settings, or null for the defaults.</param>
    /// <returns>The volumes in order; volume <c>k</c> has <c>volume_index = k</c>.</returns>
    /// <exception cref="ArgumentException">
    /// An invalid or repeated name, invalid options, or a volume limit too small for the
    /// file table or for a block.
    /// </exception>
    public static IReadOnlyList<byte[]> Create(IReadOnlyList<SarcSource> sources, string password, SarcWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrEmpty(password);
        options ??= new SarcWriterOptions();
        options.Validate();
        CheckNames(sources);

        var files = new SarcFileRecord[sources.Count];
        var blocks = new List<(int File, uint Sequence, int Offset, int Length)>();

        for (int i = 0; i < sources.Count; i++)
        {
            ReadOnlySpan<byte> data = sources[i].Data.Span;
            SarcFileType type = SarcFileClassifier.Classify(data);
            byte strategy = type == SarcFileType.AlreadyCompressed ? StrategyStored
                : options.Parse == SarcParse.Lazy ? StrategyLazy : StrategyGreedy;
            files[i] = new SarcFileRecord(sources[i].Name, (uint)data.Length, Crc32.Compute(data), type, strategy);

            // An empty file still gets one empty block, as the reference archives do.
            uint sequence = 0;
            int offset = 0;
            do
            {
                int length = Math.Min(options.BlockSize, data.Length - offset);
                blocks.Add((i, sequence++, offset, length));
                offset += length;
            }
            while (offset < data.Length);
        }

        var coded = new ReadOnlyMemory<byte>[blocks.Count];
        var flags = new byte[blocks.Count];
        Parallel.For(0, blocks.Count, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, b =>
        {
            (int file, _, int offset, int length) = blocks[b];
            ReadOnlyMemory<byte> raw = sources[file].Data.Slice(offset, length);

            if (files[file].Strategy != StrategyStored && length > 0)
            {
                byte[] encoded = SarcBlockCodec.Encode(raw.Span, options.Parse);
                if (encoded.Length < length)
                {
                    coded[b] = encoded;
                    return;
                }
            }

            coded[b] = raw;
            flags[b] = SarcPayload.NoCompression;
        });

        int[] recordSizes = new int[files.Length];
        for (int i = 0; i < files.Length; i++)
            recordSizes[i] = SarcPayload.FileRecordSize(files[i].Name);

        var items = new SarcPackItem[blocks.Count];
        for (int b = 0; b < blocks.Count; b++)
        {
            int file = blocks[b].File;
            items[b] = new SarcPackItem(file, coded[b].Length + SarcPayload.BlockRecordSize, options.Priorities[(int)files[file].FileType]);
        }

        List<SarcVolumePlan> plans = SarcVolumePacker.Plan(recordSizes, items, options.MaxVolumeBytes, options.Packing);
        if (plans.Count > ushort.MaxValue)
            throw new ArgumentException($"The archive needs {plans.Count} volumes; volume_count is 16 bits. Increase max_volume_bytes.", nameof(options));

        byte[] salt = options.FixedSalt ?? RandomNumberGenerator.GetBytes(SarcVolume.SaltSize);
        byte[] prefix = options.FixedNoncePrefix ?? RandomNumberGenerator.GetBytes(NoncePrefixSize);
        using SarcKeys keys = SarcKeys.Derive(password, salt);

        var volumes = new byte[plans.Count][];
        Span<byte> nonce = stackalloc byte[AesCtr.NonceSize];
        prefix.CopyTo(nonce);

        for (int v = 0; v < plans.Count; v++)
        {
            SarcVolumePlan plan = plans[v];
            var table = new SarcFileRecord[plan.Files.Length];
            var localIndex = new Dictionary<int, ushort>(plan.Files.Length);
            for (int i = 0; i < plan.Files.Length; i++)
            {
                table[i] = files[plan.Files[i]];
                localIndex[plan.Files[i]] = (ushort)i;
            }

            var rows = new SarcBlockRecord[plan.Blocks.Length];
            var data = new ReadOnlyMemory<byte>[plan.Blocks.Length];
            for (int i = 0; i < plan.Blocks.Length; i++)
            {
                int b = plan.Blocks[i];
                rows[i] = new SarcBlockRecord(
                    localIndex[blocks[b].File], blocks[b].Sequence, (uint)blocks[b].Length, (uint)coded[b].Length, flags[b], 0);
                data[i] = coded[b];
            }

            byte[] payload = SarcPayload.Write(table, rows, data);
            BinaryPrimitives.WriteUInt32BigEndian(nonce[NoncePrefixSize..], (uint)v);
            volumes[v] = SarcVolume.Seal(payload, (ushort)v, (ushort)plans.Count, salt, nonce, keys);
        }

        return volumes;
    }

    /// <summary>
    /// Writes an archive to disk. One volume is written to <paramref name="archivePath"/>
    /// itself; several become <c>name.part0.arc</c>, <c>name.part1.arc</c>… next to it.
    /// Nothing is written if any target already exists.
    /// </summary>
    /// <param name="archivePath">The archive, e.g. <c>backup.arc</c>.</param>
    /// <param name="sources">The files.</param>
    /// <param name="password">The password.</param>
    /// <param name="options">Settings, or null for the defaults.</param>
    /// <returns>The paths written, in volume order.</returns>
    /// <exception cref="IOException">A target file already exists.</exception>
    public static IReadOnlyList<string> CreateFiles(
        string archivePath, IReadOnlyList<SarcSource> sources, string password, SarcWriterOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        IReadOnlyList<byte[]> volumes = Create(sources, password, options);
        string[] paths = VolumePaths(archivePath, volumes.Count);

        foreach (string path in paths)
        {
            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException($"The file already exists: {path}");
        }

        for (int v = 0; v < volumes.Count; v++)
        {
            string temporary = $"{paths[v]}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(temporary, volumes[v]);
                File.Move(temporary, paths[v], overwrite: false);
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        return paths;
    }

    /// <summary>The file names <see cref="CreateFiles"/> uses for an archive of <paramref name="count"/> volumes.</summary>
    /// <param name="archivePath">The archive path as given.</param>
    /// <param name="count">Number of volumes.</param>
    /// <returns>One path per volume.</returns>
    public static string[] VolumePaths(string archivePath, int count)
    {
        if (count == 1)
            return [archivePath];

        string directory = Path.GetDirectoryName(archivePath) ?? string.Empty;
        string extension = Path.GetExtension(archivePath);
        string stem = Path.GetFileNameWithoutExtension(archivePath);
        if (extension.Length == 0)
            extension = ".arc";

        var paths = new string[count];
        for (int v = 0; v < count; v++)
            paths[v] = Path.Combine(directory, $"{stem}.part{v}{extension}");

        return paths;
    }

    private static void CheckNames(IReadOnlyList<SarcSource> sources)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (SarcSource source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (!SarcSource.IsValidName(source.Name))
                throw new ArgumentException($"'{source.Name}' cannot be stored: SARC v1 names have no '/', '\\', ':' or NUL.", nameof(sources));

            if (System.Text.Encoding.UTF8.GetByteCount(source.Name) > ushort.MaxValue)
                throw new ArgumentException($"The name '{source.Name[..32]}…' is longer than 65535 bytes.", nameof(sources));

            if (!seen.Add(source.Name))
                throw new ArgumentException($"Two files are named '{source.Name}'; SARC v1 stores names without paths.", nameof(sources));
        }
    }
}
