using UltraCompress.Core;

namespace UltraCompress.Core.Tests;

public class EntropyCalculatorTests
{
    [Fact]
    public void ShannonBitsPerByte_Empty_IsZero()
    {
        Assert.Equal(0, EntropyCalculator.ShannonBitsPerByte(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ShannonBitsPerByte_UniformByte_IsZero()
    {
        var data = new byte[256];
        Array.Fill(data, (byte)0xAB);
        Assert.Equal(0, EntropyCalculator.ShannonBitsPerByte(data));
    }

    [Fact]
    public void ShannonBitsPerByte_UniformDistribution_IsEight()
    {
        var data = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var entropy = EntropyCalculator.ShannonBitsPerByte(data);
        Assert.InRange(entropy, 7.99, 8.01);
    }

    [Fact]
    public void BlockedEntropy_SplitsOnBlockSize()
    {
        var data = new byte[10];
        Array.Fill(data, (byte)1);
        var blocks = EntropyCalculator.BlockedEntropy(data, 4);
        Assert.Equal(3, blocks.Count);
        Assert.Equal(0, blocks[0].Offset);
        Assert.Equal(4, blocks[0].Length);
        Assert.Equal(8, blocks[2].Offset);
        Assert.Equal(2, blocks[2].Length);
    }
}