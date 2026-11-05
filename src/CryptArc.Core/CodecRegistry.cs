using CryptArc.Core.Codecs;

namespace CryptArc.Core;

/// <summary>
/// Lookup for the codecs that are actually implemented right now.
/// New algorithms become available to the CLI, the GUI and the benchmarks
/// as soon as they are registered here — nothing else needs to change.
/// </summary>
public static class CodecRegistry
{
    private static readonly ICodec[] Codecs =
    [
        new StoreCodec(),
        new RleCodec(),
        new HuffmanCodec(),
        new BwtCodec(),
        new LzssCodec(),
        new LzwCodec(),
        // TODO: DeflateCodec, ArithmeticCodec
    ];

    /// <summary>Every implemented codec, in registration order.</summary>
    public static IReadOnlyList<ICodec> All => Codecs;

    /// <summary>Looks up the implementation registered for <paramref name="id"/>.</summary>
    /// <param name="id">The codec to look up.</param>
    /// <param name="codec">When this method returns, contains the codec if it is registered. This parameter is treated as uninitialized.</param>
    /// <returns><see langword="true"/> if the codec is implemented; otherwise, <see langword="false"/>.</returns>
    public static bool TryGet(CodecId id, out ICodec codec)
    {
        foreach (ICodec candidate in Codecs)
        {
            if (candidate.Id == id)
            {
                codec = candidate;
                return true;
            }
        }

        codec = default!;
        return false;
    }

    /// <exception cref="NotSupportedException">The codec is not implemented yet.</exception>
    public static ICodec Get(CodecId id) =>
        TryGet(id, out ICodec codec)
            ? codec
            : throw new NotSupportedException($"Codec '{id}' is not implemented yet.");
}
