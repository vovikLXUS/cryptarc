using System.Diagnostics;
using CryptArc.Core;
using CryptArc.Core.Analysis;
using CryptArc.Core.Hashing;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintUsage();
    return 0;
}

try
{
    return args[0] switch
    {
        "analyze" => Analyze(RequireFile(args)),
        "test" => TestCodecs(RequireFile(args)),
        _ => Fail($"Unknown command '{args[0]}'."),
    };
}
catch (Exception exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        cryptarc — smart encrypting file archiver

        Usage:
          cryptarc analyze <file>   Report entropy, repetition and the recommended codec
          cryptarc test <file>      Round-trip the file through every implemented codec

        Not implemented yet: pack, unpack, list, verify.
        """);
}

static string RequireFile(string[] args)
{
    if (args.Length < 2)
        throw new ArgumentException("A file path is required.");

    if (!File.Exists(args[1]))
        throw new FileNotFoundException($"No such file: {args[1]}");

    return args[1];
}

static int Fail(string message)
{
    Console.Error.WriteLine($"error: {message}");
    Console.Error.WriteLine();
    PrintUsage();
    return 2;
}

static int Analyze(string path)
{
    byte[] data = File.ReadAllBytes(path);
    DataProfile profile = DataAnalyzer.Analyze(data);
    CodecId recommended = DataAnalyzer.Recommend(profile);

    Console.WriteLine($"File          {Path.GetFileName(path)}");
    Console.WriteLine($"Size          {data.Length:N0} bytes");
    Console.WriteLine($"CRC-32        {Crc32.Compute(data):X8}");
    Console.WriteLine($"Sampled       {profile.SampledBytes:N0} bytes");
    Console.WriteLine($"Entropy       {profile.Entropy:F3} bits/byte  (order-0 bound: {profile.EntropyBound:P1} of original)");
    Console.WriteLine($"Repeat ratio  {profile.RepeatRatio:P1}");
    Console.WriteLine($"Text ratio    {profile.TextRatio:P1}");
    Console.WriteLine($"Recommended   {recommended}{(CodecRegistry.TryGet(recommended, out _) ? "" : "  (not implemented yet — falls back to Store)")}");

    return 0;
}

static int TestCodecs(string path)
{
    byte[] original = File.ReadAllBytes(path);
    uint expectedCrc = Crc32.Compute(original);

    Console.WriteLine($"{Path.GetFileName(path)} — {original.Length:N0} bytes");
    Console.WriteLine();
    Console.WriteLine($"{"Codec",-10} {"Compressed",14} {"Ratio",8} {"Encode",12} {"Decode",12}  Status");
    Console.WriteLine(new string('-', 74));

    int exitCode = 0;

    foreach (ICodec codec in CodecRegistry.All)
    {
        byte[] compressed = new byte[codec.MaxCompressedSize(original.Length)];

        var encodeTimer = Stopwatch.StartNew();
        int compressedLength = codec.Encode(original, compressed);
        encodeTimer.Stop();

        if (compressedLength < 0)
        {
            Console.WriteLine($"{codec.Name,-10} {"-",14} {"-",8} {"-",12} {"-",12}  buffer too small");
            exitCode = 1;
            continue;
        }

        byte[] restored = new byte[original.Length];

        var decodeTimer = Stopwatch.StartNew();
        int restoredLength = codec.Decode(compressed.AsSpan(0, compressedLength), restored);
        decodeTimer.Stop();

        bool ok = restoredLength == original.Length && Crc32.Compute(restored) == expectedCrc;
        if (!ok)
            exitCode = 1;

        double ratio = original.Length == 0 ? 1 : compressedLength / (double)original.Length;

        Console.WriteLine(
            $"{codec.Name,-10} {compressedLength,14:N0} {ratio,8:P1} " +
            $"{Throughput(original.Length, encodeTimer.Elapsed),12} " +
            $"{Throughput(original.Length, decodeTimer.Elapsed),12}  {(ok ? "ok" : "MISMATCH")}");
    }

    return exitCode;
}

static string Throughput(int bytes, TimeSpan elapsed) =>
    elapsed.TotalSeconds <= 0
        ? "—"
        : $"{bytes / elapsed.TotalSeconds / (1024 * 1024):F1} MB/s";
