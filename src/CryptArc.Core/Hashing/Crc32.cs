namespace CryptArc.Core.Hashing;

/// <summary>
/// CRC-32 (IEEE 802.3, reflected polynomial 0xEDB88320) used for block integrity.
/// </summary>
/// <remarks>
/// This is an error-detection checksum, not a security primitive — archive
/// authenticity comes from AES-GCM tags and HMAC-SHA256 instead.
/// <para>
/// Two implementations are kept on purpose: <see cref="ComputeNaive"/> processes one
/// bit at a time, <see cref="Compute"/> uses a 256-entry lookup table. They must agree
/// bit for bit; the table version is roughly 8x faster and is the reference point for
/// the optimisation write-up.
/// </para>
/// </remarks>
public static class Crc32
{
    private const uint Polynomial = 0xEDB88320u;
    private const uint Seed = 0xFFFFFFFFu;

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];

        for (uint i = 0; i < 256; i++)
        {
            uint entry = i;

            for (int bit = 0; bit < 8; bit++)
                entry = (entry & 1) != 0 ? (entry >> 1) ^ Polynomial : entry >> 1;

            table[i] = entry;
        }

        return table;
    }

    /// <summary>Table-driven CRC-32. O(n), one table lookup per byte.</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = Seed;
        uint[] table = Table;

        foreach (byte value in data)
            crc = (crc >> 8) ^ table[(crc ^ value) & 0xFF];

        return ~crc;
    }

    /// <summary>Bit-by-bit CRC-32. Kept only as the benchmark baseline.</summary>
    public static uint ComputeNaive(ReadOnlySpan<byte> data)
    {
        uint crc = Seed;

        foreach (byte value in data)
        {
            crc ^= value;

            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ Polynomial : crc >> 1;
        }

        return ~crc;
    }
}
