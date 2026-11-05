using System.Buffers.Binary;
using CryptArc.Core.IO;

namespace CryptArc.Core.Codecs;

/// <summary>
/// Adaptive order-0 arithmetic (range) coding over byte streams.
/// </summary>
/// <remarks>
/// <para>Stream format — an empty input encodes to zero bytes, anything else is:</para>
/// <list type="bullet">
///   <item><description>
///     Bytes 0…3 — uncompressed length (number of decoded symbols), 32-bit unsigned little-endian.
///   </description></item>
///   <item><description>
///     Bytes 4… — least-significant-bit bit stream packed via <see cref="BitWriter"/> and read via
///     <see cref="BitReader"/>. Contains the 32-bit arithmetic range state transitions, flushed with
///     terminating bits and zero padding up to a whole byte.
///   </description></item>
/// </list>
/// <para>
/// Adaptive model: maintains dynamic symbol frequencies for all 256 byte values starting with count 1
/// (Laplace smoothing). When the total frequency reaches <c>MaxTotal</c> (16,384), all frequencies are
/// scaled down by half (<c>(f &gt;&gt; 1) | 1</c>) to prevent integer overflow and keep the model local.
/// </para>
/// <para>
/// The optimized implementation (<see cref="Encode"/> / <see cref="Decode"/>) maintains a Fenwick tree
/// (Binary Indexed Tree) over the 256 symbol frequencies. Cumulative frequency lookup is O(log k) and
/// symbol decoding uses branchless binary lifting in exactly 8 iterations.
/// </para>
/// <para>
/// The naive baseline (<see cref="EncodeNaive"/> / <see cref="DecodeNaive"/>) uses a flat frequency array
/// with O(k) linear cumulative sum scans, producing bit-identical output.
/// </para>
/// <para>
/// Complexity, with k = 256 distinct byte symbols:
/// Encoding: O(n log k) time with Fenwick tree, O(n) space on stack.
/// Decoding: O(n log k) time with binary lifting, O(n·k) naive.
/// Space: O(1) additional heap space; all state is allocated on the stack via <c>stackalloc</c>.
/// </para>
/// </remarks>
public sealed class ArithmeticCodec : ICodec
{
    private const int SymbolCount = 256;
    private const int MaxTotal = 1 << 14; // 16,384: guarantees 32-bit range division never collapses

    private const uint Half = 0x8000_0000u;
    private const uint FirstQtr = 0x4000_0000u;
    private const uint ThirdQtr = 0xC000_0000u;

    /// <inheritdoc/>
    public CodecId Id => CodecId.Arithmetic;

    /// <inheritdoc/>
    public string Name => "Arithmetic";

    /// <inheritdoc/>
    public int MaxCompressedSize(int sourceLength) =>
        sourceLength == 0 ? 0 : sizeof(uint) + sourceLength + (sourceLength >> 4) + 64;

    /// <inheritdoc/>
    public int Encode(ReadOnlySpan<byte> source, Span<byte> destination) =>
        EncodeCore(source, destination, naive: false);

    /// <inheritdoc/>
    public int Decode(ReadOnlySpan<byte> source, Span<byte> destination) =>
        DecodeCore(source, destination, naive: false);

    /// <summary>
    /// Naive encoder baseline using an O(k) linear cumulative sum model instead of a Fenwick tree.
    /// Produces a stream bit-identical to <see cref="Encode"/>.
    /// </summary>
    public int EncodeNaive(ReadOnlySpan<byte> source, Span<byte> destination) =>
        EncodeCore(source, destination, naive: true);

    /// <summary>
    /// Naive decoder baseline using an O(k) linear search to locate each symbol.
    /// </summary>
    public int DecodeNaive(ReadOnlySpan<byte> source, Span<byte> destination) =>
        DecodeCore(source, destination, naive: true);

    private static int EncodeCore(ReadOnlySpan<byte> source, Span<byte> destination, bool naive)
    {
        if (source.IsEmpty)
            return 0;

        if (destination.Length < sizeof(uint))
            return -1;

        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)source.Length);

        var writer = new BitWriter(destination[sizeof(uint)..]);

        uint low = 0;
        uint high = 0xFFFF_FFFFu;
        int bitsToFollow = 0;

        Span<int> tree = stackalloc int[SymbolCount + 1];
        Span<int> freq = stackalloc int[SymbolCount];

        AdaptiveModel model = naive ? default : new AdaptiveModel(tree, freq);
        NaiveModel naiveModel = naive ? new NaiveModel(freq) : default;

        for (int i = 0; i < source.Length; i++)
        {
            byte symbol = source[i];

            uint cumLow;
            uint cumHigh;
            uint total;

            if (naive)
            {
                total = (uint)naiveModel.Total;
                cumLow = naiveModel.GetCum(symbol);
                cumHigh = naiveModel.GetCum(symbol + 1);
            }
            else
            {
                total = (uint)model.Total;
                cumLow = model.GetCum(symbol);
                cumHigh = model.GetCum(symbol + 1);
            }

            ulong range = (ulong)high - low + 1;
            high = low + (uint)((range * cumHigh) / total) - 1;
            low = low + (uint)((range * cumLow) / total);

            while (true)
            {
                if (high < Half)
                {
                    writer.WriteBit(false);
                    while (bitsToFollow > 0)
                    {
                        writer.WriteBit(true);
                        bitsToFollow--;
                    }
                }
                else if (low >= Half)
                {
                    writer.WriteBit(true);
                    while (bitsToFollow > 0)
                    {
                        writer.WriteBit(false);
                        bitsToFollow--;
                    }
                    low -= Half;
                    high -= Half;
                }
                else if (low >= FirstQtr && high < ThirdQtr)
                {
                    bitsToFollow++;
                    low -= FirstQtr;
                    high -= FirstQtr;
                }
                else
                {
                    break;
                }

                low <<= 1;
                high = (high << 1) | 1u;
            }

            if (naive)
                naiveModel.Update(symbol);
            else
                model.Update(symbol);
        }

        // Flush final state
        bitsToFollow++;
        if (low < FirstQtr)
        {
            writer.WriteBit(false);
            while (bitsToFollow > 0)
            {
                writer.WriteBit(true);
                bitsToFollow--;
            }
        }
        else
        {
            writer.WriteBit(true);
            while (bitsToFollow > 0)
            {
                writer.WriteBit(false);
                bitsToFollow--;
            }
        }

        // Write 31 trailing zero bits so the decoder's 32-bit register never reads past the payload
        writer.WriteBits(0, 31);

        int bitBytes = writer.Flush();
        if (bitBytes < 0)
            return -1;

        return sizeof(uint) + bitBytes;
    }

    private static int DecodeCore(ReadOnlySpan<byte> source, Span<byte> destination, bool naive)
    {
        if (source.IsEmpty)
            return 0;

        if (source.Length < sizeof(uint))
            throw new InvalidDataException("Arithmetic stream is too short for header.");

        uint originalLength = BinaryPrimitives.ReadUInt32LittleEndian(source);
        if (originalLength > (uint)destination.Length)
            throw new InvalidDataException("Arithmetic stream expands past the destination buffer.");

        if (originalLength == 0)
            return 0;

        var reader = new BitReader(source[sizeof(uint)..]);

        uint low = 0;
        uint high = 0xFFFF_FFFFu;
        uint value = 0;

        for (int b = 0; b < 32; b++)
            value = (value << 1) | (reader.ReadBit() ? 1u : 0u);

        Span<int> tree = stackalloc int[SymbolCount + 1];
        Span<int> freq = stackalloc int[SymbolCount];

        AdaptiveModel model = naive ? default : new AdaptiveModel(tree, freq);
        NaiveModel naiveModel = naive ? new NaiveModel(freq) : default;

        for (int i = 0; i < (int)originalLength; i++)
        {
            uint total = naive ? (uint)naiveModel.Total : (uint)model.Total;
            ulong range = (ulong)high - low + 1;
            ulong diff = (ulong)(value - low) + 1;
            uint target = (uint)(((diff * total) - 1) / range);

            uint cumLow;
            uint cumHigh;
            byte symbol;

            if (naive)
                symbol = naiveModel.FindSymbol(target, out cumLow, out cumHigh);
            else
                symbol = model.FindSymbol(target, out cumLow, out cumHigh);

            destination[i] = symbol;

            high = low + (uint)((range * cumHigh) / total) - 1;
            low = low + (uint)((range * cumLow) / total);

            while (true)
            {
                if (high < Half)
                {
                    // Upper half matches
                }
                else if (low >= Half)
                {
                    value -= Half;
                    low -= Half;
                    high -= Half;
                }
                else if (low >= FirstQtr && high < ThirdQtr)
                {
                    value -= FirstQtr;
                    low -= FirstQtr;
                    high -= FirstQtr;
                }
                else
                {
                    break;
                }

                low <<= 1;
                high = (high << 1) | 1u;
                value = (value << 1) | (reader.ReadBit() ? 1u : 0u);
            }

            if (naive)
                naiveModel.Update(symbol);
            else
                model.Update(symbol);
        }

        if (reader.IsOverrun)
            throw new InvalidDataException("Truncated arithmetic stream.");

        return (int)originalLength;
    }

    /// <summary>
    /// Adaptive frequency model using a Fenwick tree (Binary Indexed Tree) for O(log k) operations.
    /// </summary>
    private ref struct AdaptiveModel
    {
        private readonly Span<int> _tree;
        private readonly Span<int> _freq;
        private int _total;

        public AdaptiveModel(Span<int> tree, Span<int> freq)
        {
            _tree = tree;
            _freq = freq;
            _freq.Fill(1);
            _total = SymbolCount;

            // Initialize Fenwick tree for 256 symbols where each initial freq is 1
            for (int i = 1; i <= SymbolCount; i++)
                _tree[i] = i & -i;
        }

        public readonly int Total => _total;

        public readonly uint GetCum(int symbol)
        {
            int sum = 0;
            int idx = symbol;
            while (idx > 0)
            {
                sum += _tree[idx];
                idx -= idx & -idx;
            }
            return (uint)sum;
        }

        public readonly byte FindSymbol(uint target, out uint cumLow, out uint cumHigh)
        {
            int idx = 0;
            uint currentSum = 0;

            // Binary lifting on Fenwick tree in 8 steps
            for (int mask = 128; mask > 0; mask >>= 1)
            {
                int next = idx + mask;
                if (next <= SymbolCount && currentSum + (uint)_tree[next] <= target)
                {
                    idx = next;
                    currentSum += (uint)_tree[next];
                }
            }

            cumLow = currentSum;
            cumHigh = cumLow + (uint)_freq[idx];
            return (byte)idx;
        }

        public void Update(int symbol)
        {
            _freq[symbol]++;
            _total++;

            int idx = symbol + 1;
            while (idx <= SymbolCount)
            {
                _tree[idx]++;
                idx += idx & -idx;
            }

            if (_total >= MaxTotal)
                Rescale();
        }

        private void Rescale()
        {
            _total = 0;
            _tree.Clear();

            for (int i = 0; i < SymbolCount; i++)
            {
                _freq[i] = (_freq[i] >> 1) | 1;
                _total += _freq[i];
            }

            // O(k) linear Fenwick tree construction
            for (int i = 1; i <= SymbolCount; i++)
            {
                _tree[i] += _freq[i - 1];
                int parent = i + (i & -i);
                if (parent <= SymbolCount)
                    _tree[parent] += _tree[i];
            }
        }
    }

    /// <summary>
    /// Naive frequency model using a plain array and O(k) linear scans.
    /// </summary>
    private ref struct NaiveModel
    {
        private readonly Span<int> _freq;
        private int _total;

        public NaiveModel(Span<int> freq)
        {
            _freq = freq;
            _freq.Fill(1);
            _total = SymbolCount;
        }

        public readonly int Total => _total;

        public readonly uint GetCum(int symbol)
        {
            int sum = 0;
            for (int i = 0; i < symbol; i++)
                sum += _freq[i];
            return (uint)sum;
        }

        public readonly byte FindSymbol(uint target, out uint cumLow, out uint cumHigh)
        {
            int sum = 0;
            for (int i = 0; i < SymbolCount; i++)
            {
                int next = sum + _freq[i];
                if (target < (uint)next)
                {
                    cumLow = (uint)sum;
                    cumHigh = (uint)next;
                    return (byte)i;
                }
                sum = next;
            }

            cumLow = (uint)sum - (uint)_freq[SymbolCount - 1];
            cumHigh = (uint)sum;
            return SymbolCount - 1;
        }

        public void Update(int symbol)
        {
            _freq[symbol]++;
            _total++;

            if (_total >= MaxTotal)
            {
                _total = 0;
                for (int i = 0; i < SymbolCount; i++)
                {
                    _freq[i] = (_freq[i] >> 1) | 1;
                    _total += _freq[i];
                }
            }
        }
    }
}

