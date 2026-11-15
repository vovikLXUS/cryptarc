using System.Diagnostics;
using CryptArc.Core;
using CryptArc.Core.Analysis;
using CryptArc.Core.Container;
using CryptArc.Core.Hashing;
using CryptArc.Core.Sarc;

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
        "pack" when args.Length >= 3 => Pack(args[1], args[2..]),
        "unpack" when args.Length is 2 or 3 => Unpack(RequireFile(args), args.Length == 3 ? args[2] : "."),
        "list" when args.Length == 2 => List(RequireFile(args)),
        "verify" when args.Length == 2 => Verify(RequireFile(args)),
        "sarc-pack" when args.Length >= 3 => SarcPack(args[1], args[2..]),
        "sarc-unpack" when args.Length >= 3 => SarcUnpack(args[1], RequireFiles(args[2..])),
        "sarc-verify" when args.Length >= 2 => SarcVerify(RequireFiles(args[1..])),
        "sarc-list" when args.Length >= 2 => SarcList(RequireFiles(args[1..])),
        "pack" or "unpack" or "list" or "verify" or "sarc-pack" or "sarc-unpack" or "sarc-verify" or "sarc-list" =>
            Fail($"Wrong arguments for '{args[0]}'."),
        _ => Fail($"Unknown command '{args[0]}'."),
    };
}
catch (Exception exception)
{
    // An ArgumentException appends " (Parameter 'x')", which means nothing to a user.
    string message = exception is ArgumentException { ParamName: { } name } argument
        ? argument.Message.Replace($" (Parameter '{name}')", "", StringComparison.Ordinal)
        : exception.Message;

    Console.Error.WriteLine($"error: {message}");
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        cryptarc — smart encrypting file archiver

        Usage:
          cryptarc analyze <file>   Report entropy, repetition and the recommended codec
          cryptarc test <file>      Round-trip the file through every implemented codec
          cryptarc pack <archive> <path>...      Create an encrypted archive from files and folders
          cryptarc unpack <archive> [directory]  Restore every file; never overwrites
          cryptarc list <archive>                Show the files in an archive
          cryptarc verify <archive>              Decrypt and check everything, writing nothing

        SARC v1 archives (FORMAT_SPEC_P2, interchangeable with other teams):
          cryptarc sarc-pack <archive.arc> <path>... [options]
              --config <team_config.json>   block_size, max_volume_bytes and priorities
              --block <bytes>               block size (default 16384)
              --volume <bytes>              largest volume (default 65536)
              --greedy                      greedy LZ77 parse instead of lazy
              --greedy-packing              greedy volume packing instead of the knapsack
          cryptarc sarc-unpack <directory> <volume>...   Restore all files; volumes in any order
          cryptarc sarc-verify <volume>...               Check everything, writing nothing
          cryptarc sarc-list <volume>...                 Show the files in an archive

        A rejected SARC archive prints its code (E_SHORT, E_MAGIC, E_LENGTH, E_HMAC,
        E_PAYLOAD, E_CODE, E_CRC, E_VOLUMES) after "error:" and exits with 1.

        The password is asked for interactively, or read from a line of redirected stdin.
        """);
}

static string[] RequireFiles(string[] paths)
{
    foreach (string path in paths)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"No such file: {path}");
    }

    return paths;
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

static string ReadPassword(string prompt)
{
    if (Console.IsInputRedirected)
        return Console.ReadLine() ?? throw new ArgumentException("No password on standard input.");

    Console.Error.Write(prompt);
    var password = new System.Text.StringBuilder();

    for (ConsoleKeyInfo key = Console.ReadKey(intercept: true); key.Key != ConsoleKey.Enter; key = Console.ReadKey(intercept: true))
    {
        if (key.Key == ConsoleKey.Backspace)
            password.Length = Math.Max(0, password.Length - 1);
        else if (!char.IsControl(key.KeyChar))
            password.Append(key.KeyChar);
    }

    Console.Error.WriteLine();
    return password.ToString();
}

static int Pack(string archive, string[] paths)
{
    var sources = new List<ArchiveSource>();
    foreach (string path in paths)
    {
        foreach (ArchiveSource source in ArchiveSource.FromPath(path))
        {
            // A name that is legal here (':' on macOS, '\' on Linux) may not be portable;
            // skip that one file instead of refusing the whole archive.
            if (ArchiveSource.IsValidArchivePath(source.ArchivePath))
                sources.Add(source);
            else
                Console.Error.WriteLine($"warning: skipped '{source.ArchivePath}': archive paths cannot contain '\\', ':' or NUL");
        }
    }

    if (sources.Count == 0)
        Console.Error.WriteLine("warning: nothing to pack; writing an empty archive");

    string password = ReadPassword("Password: ");
    if (!Console.IsInputRedirected && password != ReadPassword("Repeat password: "))
        throw new ArgumentException("The passwords do not match.");

    ArchiveWriter.CreateFile(archive, sources, password);
    Console.WriteLine($"{archive}: {sources.Count:N0} files, {new FileInfo(archive).Length:N0} bytes");
    return 0;
}

static int Unpack(string archive, string directory)
{
    using ArchiveReader reader = ArchiveReader.OpenFile(archive, ReadPassword("Password: "));
    reader.ExtractAll(directory);
    Console.WriteLine($"Restored {reader.Entries.Count:N0} files to {Path.GetFullPath(directory)}");
    return 0;
}

static int List(string archive)
{
    using ArchiveReader reader = ArchiveReader.OpenFile(archive, ReadPassword("Password: "));

    Console.WriteLine($"{"Size",14} {"Stored",14}  {"Modified (UTC)",-19}  Path");
    foreach (ArchiveEntry entry in reader.Entries)
        Console.WriteLine($"{entry.OriginalSize,14:N0} {entry.StoredSize,14:N0}  {entry.ModifiedUtc,-19:yyyy-MM-dd HH:mm:ss}  {entry.Path}");

    return 0;
}

static int Verify(string archive)
{
    using ArchiveReader reader = ArchiveReader.OpenFile(archive, ReadPassword("Password: "));
    reader.Verify();
    Console.WriteLine($"OK: {reader.Entries.Count:N0} files");
    return 0;
}

static int SarcPack(string archive, string[] arguments)
{
    SarcWriterOptions options = new();
    var paths = new List<string>();

    for (int i = 0; i < arguments.Length; i++)
    {
        string argument = arguments[i];
        bool hasValue = i + 1 < arguments.Length;
        switch (argument)
        {
            case "--config" when hasValue:
                SarcWriterOptions fromConfig = SarcWriterOptions.FromTeamConfig(File.ReadAllText(arguments[++i]));
                options = fromConfig with { Parse = options.Parse, Packing = options.Packing };
                break;
            case "--block" when hasValue:
                options = options with { BlockSize = int.Parse(arguments[++i], System.Globalization.CultureInfo.InvariantCulture) };
                break;
            case "--volume" when hasValue:
                options = options with { MaxVolumeBytes = int.Parse(arguments[++i], System.Globalization.CultureInfo.InvariantCulture) };
                break;
            case "--greedy":
                options = options with { Parse = SarcParse.Greedy };
                break;
            case "--greedy-packing":
                options = options with { Packing = SarcPacking.Greedy };
                break;
            case "--config" or "--block" or "--volume":
                throw new ArgumentException($"{argument} needs a value.");
            default:
                paths.Add(argument);
                break;
        }
    }

    if (paths.Count == 0)
        throw new ArgumentException("No files to pack.");

    var sources = new List<SarcSource>();
    foreach (SarcSource source in SarcSource.FromPaths(paths))
    {
        if (SarcSource.IsValidName(source.Name))
            sources.Add(source);
        else
            Console.Error.WriteLine($"warning: skipped '{source.Name}': SARC v1 names cannot contain '/', '\\', ':' or NUL");
    }

    if (sources.Count == 0)
        Console.Error.WriteLine("warning: nothing to pack; writing an empty archive");

    string password = ReadPassword("Password: ");
    if (!Console.IsInputRedirected && password != ReadPassword("Repeat password: "))
        throw new ArgumentException("The passwords do not match.");

    var timer = Stopwatch.StartNew();
    IReadOnlyList<string> written = SarcArchiveWriter.CreateFiles(archive, sources, password, options);
    timer.Stop();

    long original = 0;
    foreach (SarcSource source in sources)
        original += source.Data.Length;

    long stored = 0;
    foreach (string path in written)
    {
        stored += new FileInfo(path).Length;
        Console.WriteLine(path);
    }

    Console.WriteLine($"{sources.Count:N0} files, {original:N0} → {stored:N0} bytes in {written.Count:N0} volumes, {timer.Elapsed.TotalSeconds:F2} s");
    return 0;
}

static int SarcUnpack(string directory, string[] volumes)
{
    IReadOnlyList<SarcFile> files = SarcArchiveReader.ReadFiles(volumes, ReadPassword("Password: "));
    SarcArchiveReader.ExtractTo(files, directory);
    Console.WriteLine($"Restored {files.Count:N0} files to {Path.GetFullPath(directory)}");
    return 0;
}

static int SarcVerify(string[] volumes)
{
    IReadOnlyList<SarcFile> files = SarcArchiveReader.ReadFiles(volumes, ReadPassword("Password: "));
    Console.WriteLine($"OK: {files.Count:N0} files in {volumes.Length:N0} volumes");
    return 0;
}

static int SarcList(string[] volumes)
{
    IReadOnlyList<SarcFile> files = SarcArchiveReader.ReadFiles(volumes, ReadPassword("Password: "));

    Console.WriteLine($"{"Size",14}  {"CRC-32",8}  {"Type",-18}  {"Strategy",8}  Name");
    foreach (SarcFile file in files)
        Console.WriteLine($"{file.Data.Length,14:N0}  {Crc32.Compute(file.Data),8:X8}  {file.FileType,-18}  {file.Strategy,8}  {file.Name}");

    return 0;
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

        // A ratio of an empty input is undefined, not 100 %: Deflate still writes 2 bytes.
        string ratio = original.Length == 0 ? "—" : (compressedLength / (double)original.Length).ToString("P1");

        Console.WriteLine(
            $"{codec.Name,-10} {compressedLength,14:N0} {ratio,8} " +
            $"{Throughput(original.Length, encodeTimer.Elapsed),12} " +
            $"{Throughput(original.Length, decodeTimer.Elapsed),12}  {(ok ? "ok" : "MISMATCH")}");
    }

    return exitCode;
}

static string Throughput(int bytes, TimeSpan elapsed) =>
    elapsed.TotalSeconds <= 0
        ? "—"
        : $"{bytes / elapsed.TotalSeconds / (1024 * 1024):F1} MB/s";
