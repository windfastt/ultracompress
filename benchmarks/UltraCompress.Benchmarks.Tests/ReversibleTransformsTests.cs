using UltraCompress.Benchmarks.Corpus;
using UltraCompress.Benchmarks.Engine;

namespace UltraCompress.Benchmarks.Tests;

public sealed class ReversibleTransformsTests
{
    [Theory]
    [InlineData(TransformKind.None)]
    [InlineData(TransformKind.Delta)]
    [InlineData(TransformKind.ByteShuffle3Delta)]
    [InlineData(TransformKind.ByteShuffle4Delta)]
    [InlineData(TransformKind.ByteShuffle8Delta)]
    [InlineData(TransformKind.ByteShuffle16Delta)]
    [InlineData(TransformKind.ByteShuffle32Delta)]
    [InlineData(TransformKind.X86Bcj)]
    public void ForwardThenInverse_IsBitExactAcrossLengthsAndAdversarialPatterns(TransformKind kind)
    {
        var rng = new DeterministicRng(0xCAFEBABE_12345678UL);
        int[] lengths = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 15, 31, 511, 512, 513, 4099, 32771];

        foreach (int len in lengths)
        {
            byte[] random = new byte[len];
            rng.FillBytes(random);
            byte[] encoded = ReversibleTransforms.ApplyForward(random, kind);
            byte[] decoded = ReversibleTransforms.ApplyInverse(encoded, kind);
            Assert.Equal(random, decoded);
        }

        // Adversarial E8/E9 opcode patterns near boundaries and overflow edges
        byte[] adversarial = new byte[257];
        for (int i = 0; i < adversarial.Length; i++)
        {
            adversarial[i] = (i % 3) switch
            {
                0 => 0xE8,
                1 => 0xE9,
                _ => 0xFF,
            };
        }

        byte[] advEnc = ReversibleTransforms.ApplyForward(adversarial, kind);
        byte[] advDec = ReversibleTransforms.ApplyInverse(advEnc, kind);
        Assert.Equal(adversarial, advDec);
    }
}
