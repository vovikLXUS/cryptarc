using System.Buffers.Binary;
using CryptArc.Core.Sarc;
using static CryptArc.Tests.Sarc.SarcTestKit;

namespace CryptArc.Tests.Sarc;

/// <summary>
/// Every failure code of FORMAT_SPEC_P2 §5, provoked on purpose. Payload faults are
/// resealed with the right keys, so they get past the HMAC as an insider's archive would.
/// </summary>
/// <remarks>
/// Layout of the payload of <see cref="OneFile"/>, named "a.txt": file entry at 4
/// (name_len 4, name 6, orig_size 11, crc32 15, file_type 19, strategy 20),
/// block_count 21, block row 23 (file_index 23, block_seq 25, raw_len 29, coded_len 33,
/// block_flags 37), block data from 38.
/// </remarks>
public class SarcRejectionTests
{
    private static readonly byte[] Text = TestData.Text();

    private static byte[] OneFile() => PackOne("a.txt", Text);

    [Fact]
    public void AVolumeShorterThan73BytesIsShort() =>
        Assert.Equal(SarcErrorCode.Short, Rejection(OneFile()[..72]));

    [Fact]
    public void ABadMagicIsMagic() =>
        Assert.Equal(SarcErrorCode.Magic, Rejection(Edited(OneFile(), v => v[1] = (byte)'X')));

    [Fact]
    public void AnotherVersionIsMagic() =>
        Assert.Equal(SarcErrorCode.Magic, Rejection(Edited(OneFile(), v => v[4] = 2)));

    [Fact]
    public void ATruncatedVolumeIsLength() =>
        Assert.Equal(SarcErrorCode.Length, Rejection(OneFile()[..^1]));

    [Fact]
    public void AnExtendedVolumeIsLength() =>
        Assert.Equal(SarcErrorCode.Length, Rejection([.. OneFile(), 0]));

    [Theory]
    [InlineData(9)]    // salt
    [InlineData(30)]   // nonce
    [InlineData(41)]   // first ciphertext byte
    [InlineData(-1)]   // last byte of the tag
    [InlineData(-40)]  // last ciphertext bytes
    public void ChangingAnyAuthenticatedByteIsHmac(int position)
    {
        byte[] volume = OneFile();
        int index = position >= 0 ? position : volume.Length + position;
        Assert.Equal(SarcErrorCode.Hmac, Rejection(Edited(volume, v => v[index] ^= 0x01)));
    }

    [Fact]
    public void AWrongPasswordIsHmac() =>
        Assert.Equal(SarcErrorCode.Hmac, Rejection(OneFile(), "not the password"));

    [Fact]
    public void ANonZeroReservedByteIsPayload() =>
        Assert.Equal(SarcErrorCode.Payload, Rejection(Reseal(OneFile(), p => Edited(p, x => x[3] = 1))));

    [Fact]
    public void ThePredictionExtensionIsPayload() =>
        Assert.Equal(SarcErrorCode.Payload, Rejection(Reseal(OneFile(), p => Edited(p, x => x[2] = 0x01))));

    [Fact]
    public void UnknownFlagBitsArePayload() =>
        Assert.Equal(SarcErrorCode.Payload, Rejection(Reseal(OneFile(), p => Edited(p, x => x[2] = 0x80))));

    [Fact]
    public void AFileTypeAboveSixIsPayload() =>
        Assert.Equal(SarcErrorCode.Payload, Rejection(Reseal(OneFile(), p => Edited(p, x => x[19] = 7))));

    [Theory]
    [InlineData("../.ss")]
    [InlineData("a/b.t")]
    [InlineData("a\\b.t")]
    [InlineData("C:abc")]
    [InlineData("..")]
    public void AnUnsafeStoredNameIsPayload(string name)
    {
        // Same byte length as "a.txt" where possible, so only the name changes.
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(name);
        byte[] volume = Reseal(OneFile(), p =>
        {
            byte[] tail = p[11..];
            byte[] head = new byte[6];
            BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(4), (ushort)bytes.Length);
            head[0] = p[0];
            head[1] = p[1];
            return [.. head, .. bytes, .. tail];
        });

        Assert.Equal(SarcErrorCode.Payload, Rejection(volume));
    }

    [Fact]
    public void ABlockNamingAMissingFileIsPayload() =>
        Assert.Equal(SarcErrorCode.Payload, Rejection(Reseal(OneFile(), p => Edited(p, x => x[24] = 1))));

    [Fact]
    public void UnknownBlockFlagsArePayload() =>
        Assert.Equal(SarcErrorCode.Payload, Rejection(Reseal(OneFile(), p => Edited(p, x => x[37] = 0x02))));

    [Fact]
    public void AStoredBlockWhoseLengthsDifferIsPayload() =>
        Assert.Equal(SarcErrorCode.Payload, Rejection(Reseal(OneFile(), p => Edited(p, x => x[37] = 0x01))));

    [Fact]
    public void TrailingBytesArePayload() =>
        Assert.Equal(SarcErrorCode.Payload, Rejection(Reseal(OneFile(), p => [.. p, 0])));

    [Fact]
    public void ATruncatedPayloadIsPayload() =>
        Assert.Equal(SarcErrorCode.Payload, Rejection(Reseal(OneFile(), p => p[..^1])));

    [Fact]
    public void AGapInBlockSequenceIsPayload() =>
        Assert.Equal(SarcErrorCode.Payload, Rejection(Reseal(OneFile(), p => Edited(p, x => x[28] = 1))));

    [Fact]
    public void BlocksThatDoNotAddUpToTheFileArePayload() =>
        Assert.Equal(SarcErrorCode.Payload, Rejection(Reseal(OneFile(), p => Edited(p, x => x[14]++))));

    [Fact]
    public void BlocksDeclaringMoreThanTheirFileArePayloadBeforeAnythingIsDecoded()
    {
        // Two blocks that each claim the whole file: caught from the tables alone.
        byte[] data = TestData.Constant(1000);
        byte[] coded = SarcBlockCodec.Encode(data);
        byte[] payload = SarcPayload.Write(
            [new SarcFileRecord("a.txt", 1000, CryptArc.Core.Hashing.Crc32.Compute(data), SarcFileType.Synthetic, 1)],
            [new SarcBlockRecord(0, 0, 1000, (uint)coded.Length, 0, 0), new SarcBlockRecord(0, 1, 1000, (uint)coded.Length, 0, 0)],
            [coded, coded]);
        SarcVolumeHeader header = SarcVolume.ReadHeader(OneFile());

        SarcException rejection = Assert.Throws<SarcException>(() => SarcArchiveReader.ReadDecrypted([header], [payload]));
        Assert.Equal(SarcErrorCode.Payload, rejection.Code);
        Assert.Contains("declare more", rejection.Message);
    }

    [Fact]
    public void AnInvalidCodeTableInsideAnArchiveIsCode()
    {
        byte[] volume = PackOne("a.txt", System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("hello world ", 40))));

        // The control block: LIT table 29 bytes, LEN table 8; the DIST entry's length is at 38 + 29 + 8 + 4.
        Assert.Equal(SarcErrorCode.Code, Rejection(Reseal(volume, p => Edited(p, x => x[38 + 29 + 8 + 4] = 16))));
    }

    [Fact]
    public void AWrongCrcIsCrc() =>
        Assert.Equal(SarcErrorCode.Crc, Rejection(Reseal(OneFile(), p => Edited(p, x => x[18] ^= 0xFF))));

    private static byte[][] ThreeVolumes() =>
        [.. Pack(new SarcWriterOptions { BlockSize = 1024, MaxVolumeBytes = 3000 }, ("m.bin", TestData.Mixed(20_000)))];

    [Fact]
    public void AMissingVolumeIsVolumes()
    {
        byte[][] volumes = ThreeVolumes();
        Assert.True(volumes.Length >= 3);
        Assert.Equal(SarcErrorCode.Volumes, Rejection(volumes.Skip(1)));
    }

    [Fact]
    public void ADuplicatedVolumeIsVolumes()
    {
        byte[][] volumes = ThreeVolumes();
        Assert.Equal(SarcErrorCode.Volumes, Rejection([.. volumes, volumes[1]]));
    }

    [Fact]
    public void VolumesOfDifferentArchivesAreVolumes()
    {
        byte[][] first = ThreeVolumes();
        byte[][] second = ThreeVolumes();
        Assert.Equal(SarcErrorCode.Volumes, Rejection([first[0], .. second[1..]]));
    }

    [Fact]
    public void AnIndexBeyondTheCountIsVolumes()
    {
        byte[][] volumes = ThreeVolumes();
        // Rewrite the last volume's index and seal it properly, so only the set is wrong.
        SarcVolumeHeader header = SarcVolume.ReadHeader(volumes[^1]);
        using SarcKeys keys = SarcKeys.Derive(Password, header.Salt);
        byte[] resealed = SarcVolume.Seal(OpenPayload(volumes[^1]), (ushort)(header.VolumeCount + 1), header.VolumeCount, header.Salt, header.Nonce, keys);

        Assert.Equal(SarcErrorCode.Volumes, Rejection([.. volumes[..^1], resealed]));
    }

    [Fact]
    public void DisagreeingVolumeCountsAreVolumes()
    {
        byte[][] volumes = ThreeVolumes();
        SarcVolumeHeader header = SarcVolume.ReadHeader(volumes[0]);
        using SarcKeys keys = SarcKeys.Derive(Password, header.Salt);
        volumes[0] = SarcVolume.Seal(OpenPayload(volumes[0]), 0, (ushort)(header.VolumeCount + 1), header.Salt, header.Nonce, keys);

        Assert.Equal(SarcErrorCode.Volumes, Rejection(volumes));
    }

    [Fact]
    public void VolumesDisagreeingAboutAFileTypeArePayload()
    {
        byte[][] volumes = ThreeVolumes();
        // Every volume lists "m.bin" alone, so its file_type is at offset 19.
        volumes[1] = Reseal(volumes[1], p => Edited(p, x => x[19] = (byte)((x[19] + 1) % 7)));

        Assert.Equal(SarcErrorCode.Payload, Rejection(volumes));
    }

    [Fact]
    public void NoVolumesAtAllIsVolumes() =>
        Assert.Equal(SarcErrorCode.Volumes, Rejection(Array.Empty<byte[]>()));

    [Fact]
    public void RandomDamageToAnAuthenticatedPayloadOnlyEverRaisesSarcException()
    {
        byte[] volume = PackOne("m.bin", TestData.Mixed(5000));
        SarcVolumeHeader header = SarcVolume.ReadHeader(volume);
        byte[] payload = OpenPayload(volume);
        var random = new Random(2026);

        // Past the HMAC, so the KDF is not repeated: thousands of payloads a second.
        for (int round = 0; round < 5000; round++)
        {
            byte[] damaged = (byte[])payload.Clone();
            int edits = random.Next(1, 4);
            for (int e = 0; e < edits; e++)
                damaged[random.Next(damaged.Length)] = (byte)random.Next(256);

            if (round % 7 == 0)
                damaged = damaged[..random.Next(damaged.Length)];

            try
            {
                SarcArchiveReader.ReadDecrypted([header], [damaged]);
            }
            catch (SarcException)
            {
            }
        }
    }
}
