using System.Text;
using CryptArc.Core.Sarc;
using static CryptArc.Tests.Sarc.SarcTestKit;

namespace CryptArc.Tests.Sarc;

/// <summary>
/// Archive → files must give the files back byte for byte, for every shape, both parsers,
/// both packers, single and multi-volume, with the volumes in any order.
/// </summary>
public class SarcRoundTripTests
{
    public static TheoryData<string, byte[]> Shapes => new()
    {
        { "empty", [] },
        { "single", [42] },
        { "shorter-than-a-match", [1, 2] },
        { "constant", TestData.Constant(50_000) },
        { "alternating", TestData.Alternating(20_000) },
        { "random", TestData.Random(40_000) },
        { "mixed", TestData.Mixed(60_000) },
        { "text", TestData.Text() },
        { "block-minus-one", TestData.Mixed(16 * 1024 - 1) },
        { "block-exact", TestData.Mixed(16 * 1024) },
        { "block-plus-one", TestData.Mixed(16 * 1024 + 1) },
        { "past-the-window", TestData.Mixed(SarcBlockCodec.Window + 300) },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void EveryShapeRoundTripsWithBothParsers(string name, byte[] data)
    {
        foreach (SarcParse parse in new[] { SarcParse.Lazy, SarcParse.Greedy })
        {
            IReadOnlyList<byte[]> volumes = Pack(SingleVolume with { Parse = parse }, (name, data));
            SarcFile file = Read(volumes).Single();
            Assert.Equal(name, file.Name);
            Assert.Equal(data, file.Data);
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void EveryShapeRoundTripsThroughTheBlockCodec(string name, byte[] data)
    {
        _ = name;
        foreach (SarcParse parse in new[] { SarcParse.Lazy, SarcParse.Greedy })
        {
            byte[] coded = SarcBlockCodec.Encode(data, parse);
            byte[] restored = new byte[data.Length];
            SarcBlockCodec.Decode(coded, restored);
            Assert.Equal(data, restored);
        }
    }

    [Fact]
    public void ManyFilesAcrossManyVolumesRestoreInAnyOrder()
    {
        (string, byte[])[] files =
        [
            ("text.txt", TestData.Text()),
            ("noise.bin", TestData.Random(30_000)),
            ("runs.dat", TestData.Constant(70_000, 0)),
            ("mixed.bin", TestData.Mixed(45_000)),
            ("empty.dat", []),
            ("один.txt", Encoding.UTF8.GetBytes("Привіт, світе!\n")),
        ];
        var options = new SarcWriterOptions { BlockSize = 4096, MaxVolumeBytes = 8192 };

        byte[][] volumes = [.. Pack(options, files)];
        Assert.True(volumes.Length > 5);
        Assert.All(volumes, v => Assert.True(v.Length <= options.MaxVolumeBytes));

        byte[][] shuffled = [.. volumes.OrderBy(v => v.Length).ThenBy(v => v[8])];
        IReadOnlyList<SarcFile> restored = Read(shuffled);

        Assert.Equal(files.Select(f => f.Item1).Order(), restored.Select(f => f.Name).Order());
        foreach ((string name, byte[] data) in files)
            Assert.Equal(data, restored.Single(f => f.Name == name).Data);
    }

    [Fact]
    public void GreedyPackingRestoresTheSameFiles()
    {
        var options = new SarcWriterOptions { BlockSize = 2048, MaxVolumeBytes = 6000, Packing = SarcPacking.Greedy };
        IReadOnlyList<byte[]> volumes = Pack(options, ("a.bin", TestData.Mixed(30_000)), ("b.txt", TestData.Text()));

        IReadOnlyList<SarcFile> restored = Read(volumes.Reverse());
        Assert.Equal(TestData.Mixed(30_000), restored.Single(f => f.Name == "a.bin").Data);
        Assert.Equal(TestData.Text(), restored.Single(f => f.Name == "b.txt").Data);
    }

    [Fact]
    public void AFileOfConstantBytesShrinksSeveralTimes()
    {
        byte[] data = TestData.Constant(100_000);
        long archived = Pack(new SarcWriterOptions(), ("same.dat", data)).Sum(v => (long)v.Length);

        Assert.True(archived * 10 < data.Length, $"{data.Length} bytes became {archived}.");
    }

    [Fact]
    public void RandomNoiseIsStoredRawWithCodedLengthEqualToRawLength()
    {
        byte[] volume = PackOne("noise.bin", TestData.Random(50_000));
        SarcPayloadContents payload = SarcPayload.Read(OpenPayload(volume));

        Assert.All(payload.Blocks, block =>
        {
            Assert.True(block.IsStored);
            Assert.Equal(block.RawLength, block.CodedLength);
        });
    }

    [Fact]
    public void AlreadyCompressedDataSkipsCompression()
    {
        byte[] gzipLike = [0x1F, 0x8B, 0x08, 0x00, .. TestData.Text()];
        byte[] volume = PackOne("data.gz", gzipLike);
        SarcPayloadContents payload = SarcPayload.Read(OpenPayload(volume));

        Assert.Equal(SarcFileType.AlreadyCompressed, payload.Files.Single().FileType);
        Assert.Equal(SarcArchiveWriter.StrategyStored, payload.Files.Single().Strategy);
        Assert.All(payload.Blocks, block => Assert.True(block.IsStored));
        Assert.Equal(gzipLike, Read([volume]).Single().Data);
    }

    [Fact]
    public void TwoArchivesOfTheSameFilesShareNoSaltOrNonce()
    {
        byte[] first = PackOne("t.txt", TestData.Text());
        byte[] second = PackOne("t.txt", TestData.Text());

        Assert.NotEqual(first[9..37], second[9..37]);
    }

    [Fact]
    public void EveryVolumeOfAnArchiveHasTheSameSaltAndItsOwnNonce()
    {
        byte[][] volumes = [.. Pack(new SarcWriterOptions { BlockSize = 1024, MaxVolumeBytes = 4096 }, ("m.bin", TestData.Mixed(40_000)))];

        Assert.All(volumes, v => Assert.Equal(volumes[0][9..25], v[9..25]));
        Assert.Equal(volumes.Length, volumes.Select(v => Convert.ToHexString(v[25..37])).Distinct().Count());
    }

    [Fact]
    public void ContentsAndNamesDoNotAppearInTheVolume()
    {
        byte[] secret = Encoding.ASCII.GetBytes("the launch code is 0000; the launch code is 0000");
        string volume = Encoding.Latin1.GetString(PackOne("secret-name.txt", secret));

        Assert.DoesNotContain("secret-name", volume);
        Assert.DoesNotContain("launch code", volume);
    }

    [Fact]
    public void ExtractionWritesFilesAndNeverOverwrites()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sarc-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            IReadOnlyList<SarcFile> files = Read(Pack(SingleVolume, ("a.txt", TestData.Text()), ("b.bin", TestData.Random(100))));
            SarcArchiveReader.ExtractTo(files, directory);
            Assert.Equal(TestData.Text(), File.ReadAllBytes(Path.Combine(directory, "a.txt")));

            File.WriteAllText(Path.Combine(directory, "a.txt"), "mine");
            File.Delete(Path.Combine(directory, "b.bin"));

            Assert.Throws<IOException>(() => SarcArchiveReader.ExtractTo(files, directory));
            Assert.Equal("mine", File.ReadAllText(Path.Combine(directory, "a.txt")));
            Assert.False(File.Exists(Path.Combine(directory, "b.bin")), "Nothing may be written when one target exists.");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("dir/file")]
    [InlineData("dir\\file")]
    [InlineData("c:file")]
    [InlineData("nul\0byte")]
    public void TheWriterRefusesUnsafeNames(string name) =>
        Assert.Throws<ArgumentException>(() => PackOne(name, [1, 2, 3]));

    [Fact]
    public void TheWriterRefusesTwoFilesWithOneName() =>
        Assert.Throws<ArgumentException>(() => Pack(SingleVolume, ("x", [1]), ("x", [2])));

    [Fact]
    public void AVolumeTooSmallForItsFileTableIsAConfigurationError() =>
        Assert.Throws<ArgumentException>(() => Pack(new SarcWriterOptions { MaxVolumeBytes = 90 }, ("a-rather-long-file-name.txt", [1])));

    [Fact]
    public void TeamConfigSuppliesSizesAndPriorities()
    {
        const string json = """
            {
              "пакування": { "max_volume_bytes": 32768, "block_size": 8192 },
              "пріоритети_типів": { "природний текст": 5, "штучні дані": 0 }
            }
            """;

        SarcWriterOptions options = SarcWriterOptions.FromTeamConfig(json);

        Assert.Equal(32768, options.MaxVolumeBytes);
        Assert.Equal(8192, options.BlockSize);
        Assert.Equal([5, 3, 2, 2, 2, 1, 0], options.Priorities);
    }
}
