namespace CryptArc.Core.Codecs;

/// <summary>
/// Pass-through codec. Used for data that is already compressed (JPEG, MP4, ZIP…)
/// where any further attempt only wastes CPU time and grows the output.
/// Also the fallback whenever a real codec fails to shrink a block.
/// </summary>
public sealed class StoreCodec : ICodec
{
    public CodecId Id => CodecId.Store;

    public string Name => "Store";

    public int MaxCompressedSize(int sourceLength) => sourceLength;

    public int Encode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.Length < source.Length)
            return -1;

        source.CopyTo(destination);
        return source.Length;
    }

    public int Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.Length < source.Length)
            throw new InvalidDataException("Destination buffer is smaller than the stored block.");

        source.CopyTo(destination);
        return source.Length;
    }
}
