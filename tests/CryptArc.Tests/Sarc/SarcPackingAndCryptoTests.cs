using CryptArc.Core.Sarc;

namespace CryptArc.Tests.Sarc;

/// <summary>The knapsack of §7 and the counter mode of §4.2.</summary>
public class SarcPackingAndCryptoTests
{
    [Fact]
    public void TheKnapsackMatchesBruteForceOnSmallSets()
    {
        var random = new Random(7);
        for (int round = 0; round < 200; round++)
        {
            int count = random.Next(1, 12);
            int[] weights = Enumerable.Range(0, count).Select(_ => random.Next(1, 60)).ToArray();
            int[] values = Enumerable.Range(0, count).Select(_ => random.Next(1, 4)).ToArray();
            int capacity = random.Next(1, 200);
            weights = weights.Select(w => Math.Min(w, capacity)).ToArray();

            bool[] chosen = SarcVolumePacker.Knapsack(weights, values, capacity);

            Assert.True(Total(weights, chosen) <= capacity);
            Assert.Equal(BruteForce(weights, values, capacity), Total(values, chosen));
        }
    }

    [Fact]
    public void TheKnapsackIsNeverWorseThanGreedy()
    {
        var random = new Random(11);
        for (int round = 0; round < 300; round++)
        {
            int count = random.Next(1, 80);
            int capacity = random.Next(100, 5000);
            int[] weights = Enumerable.Range(0, count).Select(_ => random.Next(16, capacity)).ToArray();
            int[] values = Enumerable.Range(0, count).Select(_ => random.Next(1, 4)).ToArray();

            long dp = Total(values, SarcVolumePacker.Knapsack(weights, values, capacity));
            long greedy = Total(values, SarcVolumePacker.Greedy(weights, values, capacity));

            Assert.True(dp >= greedy, $"round {round}: knapsack {dp} < greedy {greedy}");
        }
    }

    [Fact]
    public void GreedyLosesWhereTheKnapsackDoesNot()
    {
        // Greedy takes the best ratio (7/6) first and then nothing else fits; the optimum is 5 + 5.
        int[] weights = [5, 5, 6];
        int[] values = [5, 5, 7];
        Assert.Equal(10, Total(values, SarcVolumePacker.Knapsack(weights, values, 10)));
        Assert.Equal(7, Total(values, SarcVolumePacker.Greedy(weights, values, 10)));
    }

    [Fact]
    public void EveryVolumeReservesTheWholeFileTableOfPendingFiles()
    {
        int[] recordSizes = [20, 30];
        SarcPackItem[] items = [new(0, 500, 1), new(0, 500, 1), new(1, 900, 3)];

        List<SarcVolumePlan> plans = SarcVolumePacker.Plan(recordSizes, items, maxVolumeBytes: 1100, SarcPacking.Knapsack);

        // The first volume lists both files although it can hold only some of the blocks.
        Assert.Equal([0, 1], plans[0].Files);
        Assert.Equal(items.Length, plans.Sum(p => p.Blocks.Length));
        Assert.All(plans, plan =>
        {
            long used = 41 + 32 + 6 + plan.Files.Sum(f => recordSizes[f]) + plan.Blocks.Sum(b => items[b].Weight);
            Assert.True(used <= 1100);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(70_000)]
    public void BatchedCounterModeAgreesWithTheNaiveOne(int length)
    {
        byte[] key = TestData.Random(32, seed: 1);
        byte[] nonce = TestData.Random(12, seed: 2);
        byte[] input = TestData.Random(length, seed: 3);
        byte[] fast = new byte[length];
        byte[] naive = new byte[length];

        AesCtr.Transform(key, nonce, input, fast);
        AesCtr.TransformNaive(key, nonce, input, naive);

        Assert.Equal(naive, fast);

        byte[] back = new byte[length];
        AesCtr.Transform(key, nonce, fast, back);
        Assert.Equal(input, back);
    }

    [Fact]
    public void TheFirstCounterBlockIsNumberOne()
    {
        byte[] key = new byte[32];
        byte[] nonce = new byte[12];
        byte[] keystream = new byte[16];
        AesCtr.Transform(key, nonce, new byte[16], keystream);

        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        byte[] counterOne = new byte[16];
        counterOne[15] = 1;
        Assert.Equal(aes.EncryptEcb(counterOne, System.Security.Cryptography.PaddingMode.None), keystream);
    }

    [Fact]
    public void TheTwoKeysDiffer()
    {
        using SarcKeys keys = SarcKeys.Derive("algo2026", new byte[16]);
        Assert.NotEqual(keys.EncryptionKey, keys.MacKey);
    }

    private static long Total(int[] amounts, bool[] chosen)
    {
        long total = 0;
        for (int i = 0; i < amounts.Length; i++)
        {
            if (chosen[i])
                total += amounts[i];
        }

        return total;
    }

    private static long BruteForce(int[] weights, int[] values, int capacity)
    {
        long best = 0;
        for (int mask = 0; mask < 1 << weights.Length; mask++)
        {
            long weight = 0, value = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                if ((mask >> i & 1) != 0)
                {
                    weight += weights[i];
                    value += values[i];
                }
            }

            if (weight <= capacity)
                best = Math.Max(best, value);
        }

        return best;
    }
}
