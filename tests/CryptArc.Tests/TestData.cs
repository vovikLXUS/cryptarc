namespace CryptArc.Tests;

/// <summary>Deterministic sample buffers shared by the codec tests.</summary>
public static class TestData
{
    /// <summary>Incompressible: uniform random bytes from a fixed seed.</summary>
    public static byte[] Random(int length, int seed = 1234)
    {
        byte[] buffer = new byte[length];
        new Random(seed).NextBytes(buffer);
        return buffer;
    }

    /// <summary>Best case for RLE: one long run.</summary>
    public static byte[] Constant(int length, byte value = 0x42) =>
        Enumerable.Repeat(value, length).ToArray();

    /// <summary>Worst case for RLE: no two adjacent bytes are equal.</summary>
    public static byte[] Alternating(int length) =>
        Enumerable.Range(0, length).Select(i => (byte)(i % 2 == 0 ? 0xAA : 0x55)).ToArray();

    /// <summary>Realistic mixed content: runs, literals and text.</summary>
    public static byte[] Mixed(int length, int seed = 99)
    {
        var random = new Random(seed);
        var output = new List<byte>(length);

        while (output.Count < length)
        {
            if (random.Next(3) == 0)
                output.AddRange(Enumerable.Repeat((byte)random.Next(256), random.Next(3, 200)));
            else
                output.AddRange(Random(random.Next(1, 50), random.Next()));
        }

        return output.Take(length).ToArray();
    }

    public static byte[] Text() =>
        System.Text.Encoding.UTF8.GetBytes(string.Concat(
            Enumerable.Repeat("the quick brown fox jumps over the lazy dog; ", 200)));
}
