using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CryptArc.Core;
using CryptArc.Core.Container;

namespace CryptArc.Tests;

public class ArchiveTests
{
    private const string Password = "correct horse";

    /// <summary>The cheapest legal Argon2 parameters: the tests are not about KDF strength.</summary>
    private static readonly ArchiveOptions Fast = new() { KdfIterations = 1, KdfMemoryKiB = 32, BlockSize = 4096 };

    private static ArchiveSource Source(string path, byte[] contents) =>
        new(path, () => new MemoryStream(contents), new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc));

    private static byte[] Pack(ArchiveOptions options, params ArchiveSource[] sources)
    {
        using var output = new MemoryStream();
        ArchiveWriter.Create(output, sources, Password, options);
        return output.ToArray();
    }

    private static byte[] Restore(ArchiveReader reader, ArchiveEntry entry)
    {
        using var output = new MemoryStream();
        reader.Extract(entry, output);
        return output.ToArray();
    }

    private static byte[] SampleArchive() =>
        Pack(Fast, Source("a/text.txt", TestData.Text()), Source("random.bin", TestData.Random(10_000)));

    [Fact]
    public void RoundTripsFilesOfEveryShape()
    {
        (string Path, byte[] Contents)[] files =
        [
            ("empty", []),
            ("single", [7]),
            ("constant", TestData.Constant(20_000)),
            ("alternating", TestData.Alternating(9_000)),
            ("dir/random", TestData.Random(50_000)),
            ("dir/sub/mixed", TestData.Mixed(30_000)),
            ("текст/файл.txt", TestData.Text()),
            ("exact-block", TestData.Random(4096)),
        ];

        byte[] archive = Pack(Fast, files.Select(f => Source(f.Path, f.Contents)).ToArray());

        using ArchiveReader reader = ArchiveReader.Open(new MemoryStream(archive), Password);
        Assert.Equal(files.Length, reader.Entries.Count);

        for (int i = 0; i < files.Length; i++)
        {
            Assert.Equal(files[i].Path, reader.Entries[i].Path);
            Assert.Equal(files[i].Contents.Length, reader.Entries[i].OriginalSize);
            Assert.Equal(files[i].Contents, Restore(reader, reader.Entries[i]));
        }
    }

    [Fact]
    public void AnEmptyArchiveRoundTrips()
    {
        using ArchiveReader reader = ArchiveReader.Open(new MemoryStream(Pack(Fast)), Password);
        Assert.Empty(reader.Entries);
    }

    [Fact]
    public void SequentialAndParallelArchivesRestoreTheSameContents()
    {
        byte[] contents = TestData.Mixed(100_000);

        foreach (int threads in new[] { 1, 4 })
        {
            byte[] archive = Pack(Fast with { MaxDegreeOfParallelism = threads }, Source("f", contents));
            using ArchiveReader reader = ArchiveReader.Open(new MemoryStream(archive), Password);
            Assert.Equal(contents, Restore(reader, reader.Entries[0]));
        }
    }

    [Theory]
    [MemberData(nameof(RegisteredCodecs))]
    public void EveryCodecWorksInsideTheContainer(CodecId codec)
    {
        byte[] contents = TestData.Text();
        byte[] archive = Pack(Fast with { Codec = codec }, Source("f", contents));

        using ArchiveReader reader = ArchiveReader.Open(new MemoryStream(archive), Password);
        Assert.Equal(contents, Restore(reader, reader.Entries[0]));
    }

    public static TheoryData<CodecId> RegisteredCodecs()
    {
        var data = new TheoryData<CodecId>();
        foreach (ICodec codec in CodecRegistry.All)
            data.Add(codec.Id);
        return data;
    }

    [Fact]
    public void ContentsAndNamesDoNotAppearInTheFile()
    {
        byte[] archive = Pack(Fast with { Codec = CodecId.Store }, Source("secret-name.txt", TestData.Text()));

        Assert.Equal(-1, archive.AsSpan().IndexOf("quick brown"u8));
        Assert.Equal(-1, archive.AsSpan().IndexOf("secret-name"u8));
    }

    [Fact]
    public void TwoArchivesOfTheSameInputShareNoCiphertext()
    {
        ArchiveSource source = Source("f", TestData.Random(4096));
        byte[] first = Pack(Fast with { Codec = CodecId.Store }, source);
        byte[] second = Pack(Fast with { Codec = CodecId.Store }, source);

        Assert.NotEqual(first.AsSpan(16, 16).ToArray(), second.AsSpan(16, 16).ToArray());
        Assert.NotEqual(first.AsSpan(64, 64).ToArray(), second.AsSpan(64, 64).ToArray());
    }

    [Fact]
    public void AWrongPasswordIsRejected()
    {
        byte[] archive = SampleArchive();

        Assert.ThrowsAny<CryptographicException>(() =>
            ArchiveReader.Open(new MemoryStream(archive), "wrong password"));
    }

    [Fact]
    public void ComposedAndDecomposedPasswordsDeriveTheSameKey()
    {
        using var output = new MemoryStream();
        ArchiveWriter.Create(output, [Source("f", [1, 2, 3])], "пароль\u0439", Fast);

        using ArchiveReader reader = ArchiveReader.Open(new MemoryStream(output.ToArray()), "пароль\u0438\u0306");
        Assert.Single(reader.Entries);
    }

    [Theory]
    [InlineData(32)]  // kdfIterations, a downgrade attempt
    [InlineData(40)]  // blockSize
    [InlineData(44)]  // entryCount
    [InlineData(70)]  // first data block
    [InlineData(-40)] // the file table
    [InlineData(-1)]  // the trailer
    public void TamperingAnywhereIsDetected(int position)
    {
        byte[] archive = SampleArchive();
        int index = position >= 0 ? position : archive.Length + position;
        archive[index] ^= 0x01;

        Exception? exception = Record.Exception(() => ArchiveReader.Open(new MemoryStream(archive), Password));

        Assert.True(exception is CryptographicException or InvalidDataException, $"Got {exception?.GetType().Name ?? "no exception"}.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(95)]
    [InlineData(200)]
    public void TruncatedFilesAreRejected(int length)
    {
        byte[] truncated = SampleArchive().AsSpan(0, length).ToArray();

        Assert.Throws<InvalidDataException>(() => ArchiveReader.Open(new MemoryStream(truncated), Password));
    }

    [Fact]
    public void AFileCutJustBeforeItsEndIsRejected()
    {
        byte[] archive = SampleArchive();

        Assert.Throws<InvalidDataException>(() =>
            ArchiveReader.Open(new MemoryStream(archive.AsSpan(0, archive.Length - 1).ToArray()), Password));
    }

    [Fact]
    public void ForeignFilesAreRejected()
    {
        byte[] notAnArchive = Encoding.ASCII.GetBytes(new string('x', 200));

        Assert.Throws<InvalidDataException>(() => ArchiveReader.Open(new MemoryStream(notAnArchive), Password));
    }

    [Fact]
    public void AFutureFormatVersionIsRefusedByNumber()
    {
        byte[] archive = SampleArchive();
        BinaryPrimitives.WriteUInt16LittleEndian(archive.AsSpan(8), 99);

        var exception = Assert.Throws<NotSupportedException>(() => ArchiveReader.Open(new MemoryStream(archive), Password));
        Assert.Contains("99", exception.Message);
    }

    [Theory]
    [InlineData(32, 0u)]          // no KDF passes
    [InlineData(32, 1_000_000u)]  // hours of Argon2
    [InlineData(36, 0xFFFFFFFFu)] // 4 TiB of Argon2 memory
    public void AbsurdKdfParametersAreRefusedBeforeRunningTheKdf(int offset, uint value)
    {
        byte[] archive = SampleArchive();
        BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(offset), value);

        Assert.Throws<InvalidDataException>(() => ArchiveReader.Open(new MemoryStream(archive), Password));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/../../evil")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/evil")]
    [InlineData("a\\..\\evil")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("")]
    public void UnsafePathsAreRefused(string path)
    {
        Assert.False(ArchiveFormat.IsSafePath(path));
        Assert.Throws<ArgumentException>(() => Pack(Fast, Source(path, [1])));
    }

    [Fact]
    public void RepeatedPathsAreRefused() =>
        Assert.Throws<ArgumentException>(() => Pack(Fast, Source("a", [1]), Source("a", [2])));

    [Fact]
    public void AnEmptyPasswordIsRefused()
    {
        using var output = new MemoryStream();
        Assert.Throws<ArgumentException>(() => ArchiveWriter.Create(output, [], "", Fast));
    }

    [Fact]
    public void AnEntryFromAnotherArchiveIsRefused()
    {
        using ArchiveReader first = ArchiveReader.Open(new MemoryStream(SampleArchive()), Password);
        using ArchiveReader second = ArchiveReader.Open(new MemoryStream(SampleArchive()), Password);

        Assert.Throws<ArgumentException>(() => first.Extract(second.Entries[0], Stream.Null));
    }

    [Fact]
    public void FilesRoundTripThroughTheFileSystem()
    {
        string root = Directory.CreateTempSubdirectory("cryptarc-test-").FullName;
        try
        {
            string input = Path.Combine(root, "input");
            Directory.CreateDirectory(Path.Combine(input, "nested"));
            byte[] contents = TestData.Mixed(20_000);
            File.WriteAllBytes(Path.Combine(input, "nested", "data.bin"), contents);
            var modified = new DateTime(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(Path.Combine(input, "nested", "data.bin"), modified);

            string archive = Path.Combine(root, "out.cryptarc");
            ArchiveWriter.CreateFile(archive, ArchiveSource.FromPath(input), Password, Fast);

            string output = Path.Combine(root, "output");
            using (ArchiveReader reader = ArchiveReader.OpenFile(archive, Password))
            {
                reader.Verify();
                reader.ExtractAll(output);

                string restored = Path.Combine(output, "input", "nested", "data.bin");
                Assert.Equal(contents, File.ReadAllBytes(restored));
                Assert.Equal(modified, File.GetLastWriteTimeUtc(restored));

                Assert.Throws<IOException>(() => reader.ExtractAll(output));
            }

            Assert.Throws<IOException>(() =>
                ArchiveWriter.CreateFile(archive, ArchiveSource.FromPath(input), Password, Fast));
            Assert.Single(Directory.GetFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
