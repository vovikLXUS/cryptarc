using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CryptArc.Core.Sarc;

/// <summary>
/// The two keys of FORMAT_SPEC_P2 §4.1, derived once per archive:
/// <c>master = PBKDF2-HMAC-SHA256(password, salt, 100000, 64)</c>, <c>K_enc = master[0…31]</c>,
/// <c>K_mac = master[32…63]</c>.
/// </summary>
/// <remarks>
/// The password is encoded as UTF-8 exactly as given. The specification says nothing about
/// Unicode normalisation, so none is applied: a reader that normalised would derive a
/// different key from another team's archive.
/// </remarks>
internal sealed class SarcKeys : IDisposable
{
    /// <summary>PBKDF2 iteration count fixed by the specification.</summary>
    public const int Iterations = 100_000;

    /// <summary>Size of each key.</summary>
    public const int KeySize = 32;

    private SarcKeys(byte[] encryptionKey, byte[] macKey)
    {
        EncryptionKey = encryptionKey;
        MacKey = macKey;
    }

    /// <summary><c>K_enc</c>, the AES-256 key.</summary>
    public byte[] EncryptionKey { get; }

    /// <summary><c>K_mac</c>, the HMAC-SHA256 key.</summary>
    public byte[] MacKey { get; }

    /// <summary>Runs PBKDF2 and splits the result.</summary>
    /// <param name="password">The password.</param>
    /// <param name="salt">The archive's 16-byte salt.</param>
    /// <returns>The two keys.</returns>
    public static SarcKeys Derive(string password, ReadOnlySpan<byte> salt)
    {
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        byte[] master = new byte[2 * KeySize];
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, master, Iterations, HashAlgorithmName.SHA256);
            return new SarcKeys(master[..KeySize], master[KeySize..]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(master);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(EncryptionKey);
        CryptographicOperations.ZeroMemory(MacKey);
    }
}

/// <summary>
/// AES-256 in counter mode, FORMAT_SPEC_P2 §4.2: keystream block <c>i</c> is
/// <c>AES(K_enc, nonce ‖ uint32_BE(i))</c> with <c>i</c> starting at 1. Encryption and
/// decryption are the same operation.
/// </summary>
/// <remarks>
/// .NET has no CTR mode, so the counter blocks are built here and encrypted with the
/// library's AES in ECB mode — the block cipher itself is never hand-written (ADR-0017).
/// <see cref="Transform"/> encrypts many counter blocks per call; <see cref="TransformNaive"/>
/// does one block at a time and is the baseline it is checked against (hard rule 4).
/// </remarks>
internal static class AesCtr
{
    /// <summary>Size of the nonce.</summary>
    public const int NonceSize = 12;

    private const int BlockSize = 16;
    private const int BlocksPerBatch = 256;

    /// <summary>XORs <paramref name="input"/> with the keystream into <paramref name="output"/>.</summary>
    /// <param name="key">The 32-byte key.</param>
    /// <param name="nonce">The 12-byte nonce of the volume.</param>
    /// <param name="input">Plaintext or ciphertext.</param>
    /// <param name="output">Receives the result; same length as <paramref name="input"/>, may be the same memory.</param>
    public static void Transform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> input, Span<byte> output)
    {
        using Aes aes = CreateAes(key);
        Span<byte> counters = stackalloc byte[BlocksPerBatch * BlockSize];
        Span<byte> keystream = stackalloc byte[BlocksPerBatch * BlockSize];
        uint counter = 1;

        for (int offset = 0; offset < input.Length; offset += counters.Length)
        {
            int chunk = Math.Min(counters.Length, input.Length - offset);
            int blocks = (chunk + BlockSize - 1) / BlockSize;

            for (int block = 0; block < blocks; block++)
            {
                Span<byte> counterBlock = counters.Slice(block * BlockSize, BlockSize);
                nonce.CopyTo(counterBlock);
                BinaryPrimitives.WriteUInt32BigEndian(counterBlock[NonceSize..], counter++);
            }

            int bytes = blocks * BlockSize;
            aes.EncryptEcb(counters[..bytes], keystream[..bytes], PaddingMode.None);

            for (int i = 0; i < chunk; i++)
                output[offset + i] = (byte)(input[offset + i] ^ keystream[i]);
        }
    }

    /// <summary>The same transform, one AES call per 16-byte block.</summary>
    /// <param name="key">The 32-byte key.</param>
    /// <param name="nonce">The 12-byte nonce of the volume.</param>
    /// <param name="input">Plaintext or ciphertext.</param>
    /// <param name="output">Receives the result.</param>
    public static void TransformNaive(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> input, Span<byte> output)
    {
        using Aes aes = CreateAes(key);
        Span<byte> counterBlock = stackalloc byte[BlockSize];
        Span<byte> keystream = stackalloc byte[BlockSize];
        uint counter = 1;

        for (int offset = 0; offset < input.Length; offset += BlockSize)
        {
            nonce.CopyTo(counterBlock);
            BinaryPrimitives.WriteUInt32BigEndian(counterBlock[NonceSize..], counter++);
            aes.EncryptEcb(counterBlock, keystream, PaddingMode.None);

            int chunk = Math.Min(BlockSize, input.Length - offset);
            for (int i = 0; i < chunk; i++)
                output[offset + i] = (byte)(input[offset + i] ^ keystream[i]);
        }
    }

    private static Aes CreateAes(ReadOnlySpan<byte> key)
    {
        var aes = Aes.Create();
        aes.SetKey(key);
        return aes;
    }
}
