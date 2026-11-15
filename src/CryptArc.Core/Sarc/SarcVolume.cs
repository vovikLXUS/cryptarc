using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CryptArc.Core.Sarc;

/// <summary>The plaintext header of one volume, FORMAT_SPEC_P2 §1.1.</summary>
/// <param name="VolumeIndex">Number of this volume, from 0.</param>
/// <param name="VolumeCount">How many volumes the archive has.</param>
/// <param name="Salt">KDF salt, the same in every volume of an archive.</param>
/// <param name="Nonce">CTR nonce, different in every volume.</param>
/// <param name="CiphertextLength">Length of the encrypted payload.</param>
internal readonly record struct SarcVolumeHeader(
    ushort VolumeIndex, ushort VolumeCount, byte[] Salt, byte[] Nonce, uint CiphertextLength);

/// <summary>
/// One volume on disk: header, ciphertext, tag (FORMAT_SPEC_P2 §1).
/// </summary>
/// <remarks>
/// <code>
/// 0   4  magic           "SARC"
/// 4   1  version         1
/// 5   2  volume_index    uint16 BE
/// 7   2  volume_count    uint16 BE
/// 9  16  salt
/// 25 12  nonce
/// 37  4  ciphertext_len  uint32 BE
/// 41  …  ciphertext      AES-256-CTR(K_enc, nonce) of the payload
/// …  32  tag             HMAC-SHA256(K_mac, header ‖ ciphertext)
/// </code>
/// </remarks>
internal static class SarcVolume
{
    /// <summary>Size of the plaintext header.</summary>
    public const int HeaderSize = 41;

    /// <summary>Size of the HMAC tag.</summary>
    public const int TagSize = 32;

    /// <summary>Size of the salt.</summary>
    public const int SaltSize = 16;

    /// <summary>The shortest possible volume: header, empty ciphertext, tag.</summary>
    public const int MinimumSize = HeaderSize + TagSize;

    /// <summary>The only version this reader understands.</summary>
    public const byte Version = 1;

    private static ReadOnlySpan<byte> Magic => "SARC"u8;

    /// <summary>Steps 1–3 of §4.3: length, magic and version, then the declared length.</summary>
    /// <param name="volume">The whole volume file.</param>
    /// <returns>The parsed header.</returns>
    /// <exception cref="SarcException"><see cref="SarcErrorCode.Short"/>, <see cref="SarcErrorCode.Magic"/> or <see cref="SarcErrorCode.Length"/>.</exception>
    public static SarcVolumeHeader ReadHeader(ReadOnlySpan<byte> volume)
    {
        if (volume.Length < MinimumSize)
            throw new SarcException(SarcErrorCode.Short, $"The volume is {volume.Length} bytes; at least {MinimumSize} are needed.");

        if (!volume[..Magic.Length].SequenceEqual(Magic))
            throw new SarcException(SarcErrorCode.Magic, "The volume does not start with \"SARC\".");

        if (volume[4] != Version)
            throw new SarcException(SarcErrorCode.Magic, $"Format version {volume[4]} is not supported; only {Version} is.");

        uint ciphertextLength = BinaryPrimitives.ReadUInt32BigEndian(volume[37..]);
        long expected = HeaderSize + (long)ciphertextLength + TagSize;
        if (volume.Length != expected)
            throw new SarcException(SarcErrorCode.Length, $"The volume is {volume.Length} bytes; ciphertext_len {ciphertextLength} needs {expected}.");

        return new SarcVolumeHeader(
            BinaryPrimitives.ReadUInt16BigEndian(volume[5..]),
            BinaryPrimitives.ReadUInt16BigEndian(volume[7..]),
            volume.Slice(9, SaltSize).ToArray(),
            volume.Slice(25, AesCtr.NonceSize).ToArray(),
            ciphertextLength);
    }

    /// <summary>
    /// Steps 4–6 of §4.3: checks the tag in constant time and only then decrypts.
    /// </summary>
    /// <param name="volume">The whole volume, whose header has passed <see cref="ReadHeader"/>.</param>
    /// <param name="header">Its parsed header.</param>
    /// <param name="keys">The archive's keys.</param>
    /// <returns>The decrypted payload.</returns>
    /// <exception cref="SarcException"><see cref="SarcErrorCode.Hmac"/>.</exception>
    public static byte[] Open(ReadOnlySpan<byte> volume, in SarcVolumeHeader header, SarcKeys keys)
    {
        int authenticated = HeaderSize + (int)header.CiphertextLength;

        Span<byte> tag = stackalloc byte[TagSize];
        HMACSHA256.HashData(keys.MacKey, volume[..authenticated], tag);
        if (!CryptographicOperations.FixedTimeEquals(tag, volume.Slice(authenticated, TagSize)))
            throw new SarcException(SarcErrorCode.Hmac, $"Volume {header.VolumeIndex} fails its HMAC: the password is wrong or the volume was altered.");

        byte[] payload = new byte[header.CiphertextLength];
        AesCtr.Transform(keys.EncryptionKey, header.Nonce, volume[HeaderSize..authenticated], payload);
        return payload;
    }

    /// <summary>Encrypts a payload and builds the complete volume.</summary>
    /// <param name="payload">The serialised payload.</param>
    /// <param name="volumeIndex">Number of this volume.</param>
    /// <param name="volumeCount">Number of volumes in the archive.</param>
    /// <param name="salt">The archive's salt.</param>
    /// <param name="nonce">This volume's nonce.</param>
    /// <param name="keys">Keys derived from <paramref name="salt"/>.</param>
    /// <returns>The volume, ready to be written to disk.</returns>
    public static byte[] Seal(
        ReadOnlySpan<byte> payload, ushort volumeIndex, ushort volumeCount,
        ReadOnlySpan<byte> salt, ReadOnlySpan<byte> nonce, SarcKeys keys)
    {
        byte[] volume = new byte[checked(HeaderSize + payload.Length + TagSize)];
        Span<byte> span = volume;

        Magic.CopyTo(span);
        span[4] = Version;
        BinaryPrimitives.WriteUInt16BigEndian(span[5..], volumeIndex);
        BinaryPrimitives.WriteUInt16BigEndian(span[7..], volumeCount);
        salt.CopyTo(span[9..]);
        nonce.CopyTo(span[25..]);
        BinaryPrimitives.WriteUInt32BigEndian(span[37..], (uint)payload.Length);

        AesCtr.Transform(keys.EncryptionKey, nonce, payload, span.Slice(HeaderSize, payload.Length));

        int authenticated = HeaderSize + payload.Length;
        HMACSHA256.HashData(keys.MacKey, span[..authenticated], span.Slice(authenticated, TagSize));
        return volume;
    }
}
