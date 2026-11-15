using System.Buffers.Binary;
using CryptArc.Core.Sarc;

namespace CryptArc.Tests.Sarc;

/// <summary>Helpers shared by the SARC tests: packing, reading, and resealing a doctored payload.</summary>
internal static class SarcTestKit
{
    public const string Password = "algo2026";

    public static readonly SarcWriterOptions SingleVolume = new() { MaxVolumeBytes = 16 * 1024 * 1024 };

    public static IReadOnlyList<byte[]> Pack(SarcWriterOptions options, params (string Name, byte[] Data)[] files) =>
        SarcArchiveWriter.Create(files.Select(f => new SarcSource(f.Name, f.Data)).ToArray(), Password, options);

    public static byte[] PackOne(string name, byte[] data) => Pack(SingleVolume, (name, data)).Single();

    public static IReadOnlyList<SarcFile> Read(IEnumerable<byte[]> volumes, string password = Password) =>
        SarcArchiveReader.Read(volumes.Select(v => (ReadOnlyMemory<byte>)v).ToArray(), password);

    public static SarcErrorCode Rejection(IEnumerable<byte[]> volumes, string password = Password) =>
        Assert.Throws<SarcException>(() => Read(volumes, password)).Code;

    public static SarcErrorCode Rejection(byte[] volume, string password = Password) => Rejection([volume], password);

    /// <summary>The decrypted payload of a volume made with <see cref="Password"/>.</summary>
    public static byte[] OpenPayload(byte[] volume)
    {
        SarcVolumeHeader header = SarcVolume.ReadHeader(volume);
        using SarcKeys keys = SarcKeys.Derive(Password, header.Salt);
        return SarcVolume.Open(volume, header, keys);
    }

    /// <summary>
    /// Decrypts a volume, lets <paramref name="edit"/> change the payload, and seals the
    /// result with the right keys again — a structurally broken payload that still passes
    /// the HMAC, which is what a reader must survive after authentication.
    /// </summary>
    public static byte[] Reseal(byte[] volume, Func<byte[], byte[]> edit)
    {
        SarcVolumeHeader header = SarcVolume.ReadHeader(volume);
        using SarcKeys keys = SarcKeys.Derive(Password, header.Salt);
        byte[] payload = edit(SarcVolume.Open(volume, header, keys));
        return SarcVolume.Seal(payload, header.VolumeIndex, header.VolumeCount, header.Salt, header.Nonce, keys);
    }

    public static byte[] Edited(byte[] bytes, Action<byte[]> edit)
    {
        byte[] copy = (byte[])bytes.Clone();
        edit(copy);
        return copy;
    }

    /// <summary>A serialised code table from (symbol, length) pairs, in the order given.</summary>
    public static byte[] Table(params (int Symbol, int Length)[] entries)
    {
        byte[] table = new byte[2 + 3 * entries.Length];
        BinaryPrimitives.WriteUInt16BigEndian(table, (ushort)entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(2 + 3 * i), (ushort)entries[i].Symbol);
            table[4 + 3 * i] = (byte)entries[i].Length;
        }

        return table;
    }

    /// <summary>A coded block assembled from its parts.</summary>
    public static byte[] Block(byte[] lit, byte[] len, byte[] dist, uint tokenCount, byte[] stream, uint? streamLength = null)
    {
        byte[] counts = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(counts, tokenCount);
        BinaryPrimitives.WriteUInt32BigEndian(counts.AsSpan(4), streamLength ?? (uint)stream.Length);
        return [.. lit, .. len, .. dist, .. counts, .. stream];
    }

    public static byte[] FromHex(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));
}
