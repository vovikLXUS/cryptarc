using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace CryptArc.Core.Container;

/// <summary>
/// The two keys derived from a password, as specified in docs/container-format.md.
/// All primitives come from libraries; nothing here is hand-written cryptography
/// (hard rule 2).
/// </summary>
internal sealed class ArchiveKeys : IDisposable
{
    private const int KeySize = 32;
    private const int ArgonParallelism = 4;

    private ArchiveKeys(byte[] contentKey, byte[] macKey)
    {
        ContentKey = contentKey;
        MacKey = macKey;
    }

    public byte[] ContentKey { get; }

    public byte[] MacKey { get; }

    /// <summary>
    /// Runs Argon2id and expands the result with HKDF. The password is normalised to
    /// NFC first, so the same letters typed on systems that compose them differently
    /// (the Ukrainian "й" and "ї" among them) produce the same key.
    /// </summary>
    public static ArchiveKeys Derive(string password, ReadOnlySpan<byte> salt, uint iterations, uint memoryKiB)
    {
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));
        byte[]? masterKey = null;
        try
        {
            using var argon = new Argon2id(passwordBytes)
            {
                Salt = salt.ToArray(),
                DegreeOfParallelism = ArgonParallelism,
                Iterations = checked((int)iterations),
                MemorySize = checked((int)memoryKiB),
            };

            masterKey = argon.GetBytes(KeySize);

            return new ArchiveKeys(
                Expand(masterKey, "cryptarc:content:v1"),
                Expand(masterKey, "cryptarc:mac:v1"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            if (masterKey is not null)
                CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(ContentKey);
        CryptographicOperations.ZeroMemory(MacKey);
    }

    private static byte[] Expand(byte[] masterKey, string info) =>
        HKDF.Expand(HashAlgorithmName.SHA256, masterKey, KeySize, Encoding.ASCII.GetBytes(info));
}
