using System.Buffers;
using System.Security.Cryptography;
using CryptArc.Core.Blocks;
using CryptArc.Core.Hashing;

namespace CryptArc.Core.Container;

/// <summary>
/// Writes encrypted <c>.cryptarc</c> archives. The format is specified in
/// docs/container-format.md.
/// </summary>
/// <remarks>
/// Each block is compressed, then encrypted (ADR-0006). Blocks are read in batches of
/// twice the thread count, processed in parallel and written in order, so memory stays
/// bounded whatever the input size. <see cref="ArchiveOptions.MaxDegreeOfParallelism"/>
/// of one runs the same work on the calling thread, as the naive baseline.
/// </remarks>
public static class ArchiveWriter
{
    private const CodecId TableCodec = CodecId.Deflate;

    /// <summary>
    /// Creates an archive file. It appears at <paramref name="path"/> only once it is
    /// complete, and an existing file is never replaced.
    /// </summary>
    /// <param name="path">Where to create the archive.</param>
    /// <param name="sources">The files to add, in archive order.</param>
    /// <param name="password">The password; must not be empty.</param>
    /// <param name="options">Settings, or null for the defaults.</param>
    /// <param name="cancellationToken">Stops the run between blocks.</param>
    /// <exception cref="IOException">A file already exists at <paramref name="path"/>.</exception>
    /// <exception cref="ArgumentException">A source path is unsafe or repeated, or the password is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public static void CreateFile(
        string path,
        IReadOnlyList<ArchiveSource> sources,
        string password,
        ArchiveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(path))
            throw new IOException($"The file already exists: {path}");

        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                Create(stream, sources, password, options, cancellationToken);

            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>Writes an archive to a stream, replacing anything already in it.</summary>
    /// <param name="destination">A seekable, readable and writable stream; left open.</param>
    /// <param name="sources">The files to add, in archive order.</param>
    /// <param name="password">The password; must not be empty.</param>
    /// <param name="options">Settings, or null for the defaults.</param>
    /// <param name="cancellationToken">Stops the run between blocks.</param>
    /// <exception cref="ArgumentException">
    /// A source path is unsafe or repeated, the password is empty, or the stream cannot
    /// seek, read and write.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public static void Create(
        Stream destination,
        IReadOnlyList<ArchiveSource> sources,
        string password,
        ArchiveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrEmpty(password);
        options ??= new ArchiveOptions();

        if (!destination.CanSeek || !destination.CanRead || !destination.CanWrite)
            throw new ArgumentException("The stream must be seekable, readable and writable.", nameof(destination));

        Validate(sources, options);

        byte[] salt = RandomNumberGenerator.GetBytes(ArchiveFormat.SaltSize);
        using ArchiveKeys keys = ArchiveKeys.Derive(password, salt, options.KdfIterations, options.KdfMemoryKiB);

        destination.SetLength(0);
        destination.Write(new byte[ArchiveFormat.HeaderSize]);

        var entries = new List<ArchiveEntry>(sources.Count);
        var blocks = new List<BlockRecord>();
        WriteBlocks(destination, sources, options, keys.ContentKey, entries, blocks, cancellationToken);

        long tableOffset = destination.Position;
        long tableLength = WriteTable(destination, entries, blocks, keys.ContentKey);

        Span<byte> header = stackalloc byte[ArchiveFormat.HeaderSize];
        ArchiveFormat.WriteHeader(header, new ArchiveHeader(
            salt, options.KdfIterations, options.KdfMemoryKiB, (uint)options.BlockSize,
            (uint)entries.Count, tableOffset, tableLength));
        destination.Position = 0;
        destination.Write(header);

        // The header is only final now, so the MAC is one pass over the finished file.
        long macOffset = tableOffset + tableLength;
        Span<byte> mac = stackalloc byte[ArchiveFormat.MacSize];
        destination.Flush();
        ArchiveFormat.ComputeMac(destination, macOffset, keys.MacKey, mac, cancellationToken);
        destination.Position = macOffset;
        destination.Write(mac);
        destination.Flush();
    }

    private static void Validate(IReadOnlyList<ArchiveSource> sources, ArchiveOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.BlockSize, BlockCompressor.MinBlockSize, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.BlockSize, BlockCompressor.MaxBlockSize, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxDegreeOfParallelism, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.KdfIterations, ArchiveFormat.MinKdfIterations, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.KdfIterations, ArchiveFormat.MaxKdfIterations, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.KdfMemoryKiB, ArchiveFormat.MinKdfMemoryKiB, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.KdfMemoryKiB, ArchiveFormat.MaxKdfMemoryKiB, nameof(options));

        if (options.Codec is { } codec && !CodecRegistry.TryGet(codec, out _))
            throw new ArgumentException($"Codec {codec} is not implemented.", nameof(options));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ArchiveSource source in sources)
        {
            if (!ArchiveFormat.IsSafePath(source.ArchivePath) || System.Text.Encoding.UTF8.GetByteCount(source.ArchivePath) > ushort.MaxValue)
                throw new ArgumentException($"Unsafe archive path: '{source.ArchivePath}'.", nameof(sources));

            if (!seen.Add(source.ArchivePath))
                throw new ArgumentException($"Repeated archive path: '{source.ArchivePath}'.", nameof(sources));
        }
    }

    private static void WriteBlocks(
        Stream destination,
        IReadOnlyList<ArchiveSource> sources,
        ArchiveOptions options,
        byte[] contentKey,
        List<ArchiveEntry> entries,
        List<BlockRecord> blocks,
        CancellationToken cancellationToken)
    {
        var jobs = new BlockJob[Math.Max(1, options.MaxDegreeOfParallelism * 2)];
        for (int i = 0; i < jobs.Length; i++)
            jobs[i] = new BlockJob(options.BlockSize);

        try
        {
            ICodec? fixedCodec = options.Codec is { } id ? CodecRegistry.Get(id) : null;
            int pending = 0;
            uint nextBlock = 0;
            var finished = new List<(ArchiveSource Source, long Size, byte[] Hash, uint First, uint Count)>(sources.Count);

            foreach (ArchiveSource source in sources)
            {
                uint firstBlock = nextBlock;
                long originalSize = 0;

                using Stream input = source.OpenRead();
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    BlockJob job = jobs[pending];
                    int read = input.ReadAtLeast(job.Plain.AsSpan(0, options.BlockSize), options.BlockSize, throwOnEndOfStream: false);
                    if (read == 0)
                        break;

                    if (nextBlock == ArchiveFormat.TableAad - 1)
                        throw new InvalidOperationException("The archive has too many blocks.");

                    hash.AppendData(job.Plain, 0, read);
                    originalSize += read;
                    job.Assign(nextBlock++, read);

                    if (++pending == jobs.Length)
                    {
                        Flush(destination, jobs, pending, options, fixedCodec, contentKey, blocks, cancellationToken);
                        pending = 0;
                    }

                    if (read < options.BlockSize)
                        break;
                }

                finished.Add((source, originalSize, hash.GetHashAndReset(), firstBlock, nextBlock - firstBlock));
            }

            Flush(destination, jobs, pending, options, fixedCodec, contentKey, blocks, cancellationToken);

            // Stored sizes are known only once the last batch has been compressed.
            for (int i = 0; i < finished.Count; i++)
            {
                (ArchiveSource source, long size, byte[] sha256, uint first, uint count) = finished[i];

                long stored = 0;
                for (uint b = first; b < first + count; b++)
                    stored += blocks[(int)b].StoredSize;

                entries.Add(new ArchiveEntry(
                    i, source.ArchivePath, size, stored, first, count,
                    source.ModifiedUtc.ToUniversalTime().Ticks, (uint)source.Attributes, sha256));
            }
        }
        finally
        {
            foreach (BlockJob? job in jobs)
                job?.Dispose();
        }
    }

    private static void Flush(
        Stream destination,
        BlockJob[] jobs,
        int count,
        ArchiveOptions options,
        ICodec? fixedCodec,
        byte[] contentKey,
        List<BlockRecord> blocks,
        CancellationToken cancellationToken)
    {
        if (options.MaxDegreeOfParallelism == 1)
        {
            for (int i = 0; i < count; i++)
                jobs[i].Seal(fixedCodec, options.Verify, contentKey);
        }
        else
        {
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                CancellationToken = cancellationToken,
            };
            Parallel.For(0, count, parallelOptions, i => jobs[i].Seal(fixedCodec, options.Verify, contentKey));
        }

        for (int i = 0; i < count; i++)
        {
            BlockJob job = jobs[i];
            long offset = destination.Position;
            destination.Write(job.Output, 0, job.StoredSize + ArchiveFormat.BlockOverhead);
            blocks.Add(new BlockRecord(offset, (uint)job.StoredSize, (uint)job.Length, job.Codec, job.Crc));
        }
    }

    private static long WriteTable(Stream destination, List<ArchiveEntry> entries, List<BlockRecord> blocks, byte[] contentKey)
    {
        byte[] table = ArchiveFormat.SerializeTable(entries, blocks);

        const int PrefixSize = 5;
        int headSize = ArchiveFormat.NonceSize + PrefixSize;
        byte[] output = new byte[headSize + table.Length + ArchiveFormat.TagSize];

        ICodec codec = CodecRegistry.Get(TableCodec);
        Span<byte> body = output.AsSpan(headSize, table.Length);
        int compressed = codec.Encode(table, body);
        if (compressed < 0)
        {
            codec = CodecRegistry.Get(CodecId.Store);
            compressed = codec.Encode(table, body);
        }

        Span<byte> plaintext = output.AsSpan(ArchiveFormat.NonceSize, PrefixSize + compressed);
        plaintext[0] = (byte)codec.Id;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(plaintext[1..], table.Length);

        Span<byte> nonce = output.AsSpan(0, ArchiveFormat.NonceSize);
        RandomNumberGenerator.Fill(nonce);

        Span<byte> aad = stackalloc byte[sizeof(uint)];
        ArchiveFormat.WriteAad(aad, ArchiveFormat.TableAad);

        using var aes = new AesGcm(contentKey, ArchiveFormat.TagSize);
        aes.Encrypt(nonce, plaintext, plaintext, output.AsSpan(ArchiveFormat.NonceSize + plaintext.Length, ArchiveFormat.TagSize), aad);

        int length = ArchiveFormat.NonceSize + plaintext.Length + ArchiveFormat.TagSize;
        destination.Write(output, 0, length);
        return length;
    }

    /// <summary>One block's buffers. Workers touch only their own job, so nothing is shared.</summary>
    private sealed class BlockJob(int blockSize) : IDisposable
    {
        public byte[] Plain { get; } = ArrayPool<byte>.Shared.Rent(blockSize);

        public byte[] Output { get; } = ArrayPool<byte>.Shared.Rent(blockSize + ArchiveFormat.BlockOverhead);

        public uint Index { get; private set; }

        public int Length { get; private set; }

        public int StoredSize { get; private set; }

        public CodecId Codec { get; private set; }

        public uint Crc { get; private set; }

        public void Assign(uint index, int length)
        {
            Index = index;
            Length = length;
        }

        /// <summary>Compresses, then encrypts in place; the output is nonce, ciphertext, tag.</summary>
        public void Seal(ICodec? fixedCodec, bool verify, byte[] contentKey)
        {
            ReadOnlySpan<byte> plain = Plain.AsSpan(0, Length);
            Span<byte> body = Output.AsSpan(ArchiveFormat.NonceSize, Length);

            Crc = Crc32.Compute(plain);

            ICodec codec = fixedCodec ?? BlockCompressor.ChooseCodec(plain);
            int size = codec.Encode(plain, body);

            if (size < 0 || (verify && !BlockCompressor.RoundTrips(codec, plain, body[..size])))
            {
                codec = CodecRegistry.Get(CodecId.Store);
                plain.CopyTo(body);
                size = Length;
            }

            Codec = codec.Id;
            StoredSize = size;

            Span<byte> nonce = Output.AsSpan(0, ArchiveFormat.NonceSize);
            RandomNumberGenerator.Fill(nonce);

            Span<byte> aad = stackalloc byte[sizeof(uint)];
            ArchiveFormat.WriteAad(aad, Index);

            Span<byte> payload = body[..size];
            using var aes = new AesGcm(contentKey, ArchiveFormat.TagSize);
            aes.Encrypt(nonce, payload, payload, Output.AsSpan(ArchiveFormat.NonceSize + size, ArchiveFormat.TagSize), aad);
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(Plain, clearArray: true);
            ArrayPool<byte>.Shared.Return(Output, clearArray: true);
        }
    }
}
