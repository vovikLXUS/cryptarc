using System.Text.Json;

namespace CryptArc.Core.Sarc;

/// <summary>Settings for <see cref="SarcArchiveWriter"/>.</summary>
public sealed record SarcWriterOptions
{
    /// <summary>Size of the blocks a file is cut into before coding. Defaults to 16 KiB.</summary>
    public int BlockSize { get; init; } = 16 * 1024;

    /// <summary>
    /// Largest volume the packer writes, header and tag included. Defaults to 64 KiB.
    /// A reader accepts volumes of any size; this limits only the writer (§7).
    /// </summary>
    public int MaxVolumeBytes { get; init; } = 64 * 1024;

    /// <summary>How LZ77 tokens are chosen. Defaults to lazy matching.</summary>
    public SarcParse Parse { get; init; } = SarcParse.Lazy;

    /// <summary>How blocks are spread over volumes. Defaults to the exact knapsack.</summary>
    public SarcPacking Packing { get; init; } = SarcPacking.Knapsack;

    /// <summary>
    /// Value of a block of each file type for the knapsack, indexed by <see cref="SarcFileType"/>.
    /// Defaults to the <c>пріоритети_типів</c> issued with Project 2: 3, 3, 2, 2, 2, 1, 1.
    /// </summary>
    public IReadOnlyList<int> Priorities { get; init; } = [3, 3, 2, 2, 2, 1, 1];

    /// <summary>Worker threads for coding blocks. Defaults to the number of logical processors.</summary>
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>
    /// Fixed salt for reproducing the specification's control example. Never set in real
    /// use: every archive must get a fresh random salt (§4.2).
    /// </summary>
    internal byte[]? FixedSalt { get; init; }

    /// <summary>Fixed 8-byte nonce prefix, for the control example only.</summary>
    internal byte[]? FixedNoncePrefix { get; init; }

    /// <summary>
    /// Reads <c>block_size</c>, <c>max_volume_bytes</c> and <c>пріоритети_типів</c> from a
    /// <c>team_config.json</c>; anything missing keeps its default.
    /// </summary>
    /// <param name="json">The file's contents.</param>
    /// <returns>The options.</returns>
    /// <exception cref="JsonException">The text is not JSON.</exception>
    /// <exception cref="ArgumentException">A priority names a type that does not exist.</exception>
    public static SarcWriterOptions FromTeamConfig(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        var options = new SarcWriterOptions();

        if (root.TryGetProperty("пакування", out JsonElement packing))
        {
            if (packing.TryGetProperty("block_size", out JsonElement blockSize))
                options = options with { BlockSize = blockSize.GetInt32() };

            if (packing.TryGetProperty("max_volume_bytes", out JsonElement maxVolume))
                options = options with { MaxVolumeBytes = maxVolume.GetInt32() };
        }

        if (root.TryGetProperty("пріоритети_типів", out JsonElement priorities))
        {
            int[] values = [.. options.Priorities];
            foreach (JsonProperty entry in priorities.EnumerateObject())
            {
                int type = Array.IndexOf(TypeNames, entry.Name);
                if (type < 0)
                    throw new ArgumentException($"Unknown file type '{entry.Name}' in пріоритети_типів.", nameof(json));

                values[type] = entry.Value.GetInt32();
            }

            options = options with { Priorities = values };
        }

        return options;
    }

    /// <summary>The Ukrainian names of the seven types, as <c>team_config.json</c> spells them.</summary>
    internal static readonly string[] TypeNames =
    [
        "природний текст", "вихідний код", "структуровані дані", "растр без стиснення",
        "виконуваний файл", "вже стиснуте", "штучні дані",
    ];

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(BlockSize, 1, nameof(BlockSize));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(BlockSize, 16 * 1024 * 1024, nameof(BlockSize));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxVolumeBytes, SarcVolume.MinimumSize + SarcPayload.FixedFieldsSize, nameof(MaxVolumeBytes));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDegreeOfParallelism, 1, nameof(MaxDegreeOfParallelism));
        if (Priorities.Count != SarcPayload.MaxFileType + 1)
            throw new ArgumentException($"Priorities needs {SarcPayload.MaxFileType + 1} values, one per file type.", nameof(Priorities));
    }
}
