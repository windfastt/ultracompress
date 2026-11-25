using System.Buffers.Binary;
using UltraCompress.Core;
using UltraCompress.Core.Transforms;
using UltraCompress.Worker.Backends;

namespace UltraCompress.SafetyAndProfiles.Tests;

public sealed class LosslessTransformsTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void DeltaTransform_RoundtripsBitExact_AcrossBoundaryLengthsAndFuzzPayloads(int stride)
    {
        var transform = new DeltaTransform(stride);

        foreach (var len in new[] { 0, 1, 2, 3, 4, 7, 8, 9, 15, 16, 17, 255, 4099 })
        {
            var original = CreateDeterministicBytes(len, seed: stride * 1000 + len);
            var encoded = transform.Encode(original);

            Assert.Equal(TransformFrameHeader.HeaderSizeBytes + original.Length, encoded.Length);
            var header = TransformFrameHeader.ReadAndValidate(encoded, TransformKind.Delta);
            Assert.Equal(stride, header.Parameter);
            Assert.Equal(original.Length, header.OriginalLength);

            var decoded = transform.Decode(encoded);
            Assert.Equal(original, decoded);
            Assert.Equal(original, TransformPipeline.DecodeFramed(encoded));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(16)]
    [InlineData(-1)]
    public void DeltaTransform_RejectsUnsupportedStrides(int badStride)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeltaTransform(badStride));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void ByteShuffleTransform_RoundtripsBitExact_AcrossAlignedAndUnalignedLengths(int stride)
    {
        var transform = new ByteShuffleTransform(stride);

        foreach (var len in new[] { 0, 1, 3, 4, 5, 7, 8, 11, 16, 33, 8197 })
        {
            var original = CreateDeterministicBytes(len, seed: stride * 500 + len);
            var encoded = transform.Encode(original);

            Assert.Equal(TransformFrameHeader.HeaderSizeBytes + original.Length, encoded.Length);
            var header = TransformFrameHeader.ReadAndValidate(encoded, TransformKind.ByteShuffle);
            Assert.Equal(stride, header.Parameter);
            Assert.Equal(original.Length, header.OriginalLength);

            var decoded = transform.Decode(encoded);
            Assert.Equal(original, decoded);
            Assert.Equal(original, TransformPipeline.DecodeFramed(encoded));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(16)]
    public void ByteShuffleTransform_RejectsUnsupportedStrides(int badStride)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ByteShuffleTransform(badStride));
    }

    [Fact]
    public async Task ByteShuffleAndDelta_SubstantiallyBeatRawZstd_OnStructuredNumericRecords()
    {
        // 8,192 float64/int64 tabular records (64 KiB) with slowly varying counters and timestamps
        const int recordCount = 8192;
        var tabular = new byte[recordCount * 8];
        for (var i = 0; i < recordCount; i++)
        {
            var value = 1_700_000_000_000_000L + i * 250L + (i % 7);
            BinaryPrimitives.WriteInt64LittleEndian(tabular.AsSpan(i * 8, 8), value);
        }

        var zstd = new ZstdCompressionBackend();
        var step = new CompressStep { Codec = "zstd", Level = 9 };

        var rawCompressed = await zstd.CompressAsync(tabular, step);
        var shuffled = new ByteShuffleTransform(8).Encode(tabular);
        var shuffledCompressed = await zstd.CompressAsync(shuffled, step);
        var delta = new DeltaTransform(8).Encode(tabular);
        var deltaCompressed = await zstd.CompressAsync(delta, step);

        Assert.True(
            shuffledCompressed.Length < rawCompressed.Length * 0.70,
            $"Expected ByteShuffle+Zstd ({shuffledCompressed.Length} B) to be < 70% of Raw+Zstd ({rawCompressed.Length} B).");
        Assert.True(
            deltaCompressed.Length < rawCompressed.Length * 0.50,
            $"Expected Delta8+Zstd ({deltaCompressed.Length} B) to be < 50% of Raw+Zstd ({rawCompressed.Length} B).");

        // Verify exact restoration
        var restoredShuffle = new ByteShuffleTransform(8).Decode(await zstd.DecompressAsync(shuffledCompressed, step));
        var restoredDelta = new DeltaTransform(8).Decode(await zstd.DecompressAsync(deltaCompressed, step));
        Assert.Equal(tabular, restoredShuffle);
        Assert.Equal(tabular, restoredDelta);
    }

    [Fact]
    public async Task X86BcjTransform_RoundtripsBitExact_OnSyntheticPeCodeAdversarialOverlapsAndFuzz()
    {
        var bcj = X86BcjTransform.Instance;

        // 1. Boundary & fuzz lengths including overlapping 0xE8/0xE9 sequences
        foreach (var len in new[] { 0, 1, 4, 5, 6, 9, 10, 31, 4096, 16_389 })
        {
            var fuzz = CreateDeterministicBytes(len, seed: 9001 + len);
            var encoded = bcj.Encode(fuzz);
            Assert.Equal(fuzz, bcj.Decode(encoded));
        }

        // Explicit adversarial overlapping E8/E9 sequences and +/-16 MiB wraparound values
        byte[] adversarial =
        [
            0xE8, 0xE8, 0x00, 0x00, 0x42, 0x00, // outer E8 has MSB 0x42, inner byte is E8
            0xE8, 0xFF, 0xFF, 0xFF, 0xFF,       // rel = -1
            0xE9, 0xFE, 0xFF, 0xFF, 0x00,       // near +16 MiB boundary
            0xE8, 0x05, 0x00, 0x00, 0xFF,       // near -16 MiB boundary
            0xE9, 0xE8, 0xE9, 0xE8, 0x00,       // nested opcodes inside operand with MSB 0x00
            0xE8, 0x12, 0x34,             // trailing < 5 bytes at EOF
        ];
        Assert.Equal(adversarial, bcj.Decode(bcj.Encode(adversarial)));

        // 2. Synthetic PE/ELF x86 text section with repeated CALLs to shared runtime helpers
        var x86Code = BuildSyntheticX86ExecutableSection(sizeBytes: 65_536);
        var bcjEncoded = bcj.Encode(x86Code);
        Assert.Equal(x86Code, bcj.Decode(bcjEncoded));

        var zstd = new ZstdCompressionBackend();
        var step = new CompressStep { Codec = "zstd", Level = 9 };
        var rawComp = await zstd.CompressAsync(x86Code, step);
        var bcjComp = await zstd.CompressAsync(bcjEncoded, step);

        Assert.True(
            bcjComp.Length < rawComp.Length,
            $"Expected X86Bcj+Zstd ({bcjComp.Length} B) < Raw+Zstd ({rawComp.Length} B) on x86 CALL/JMP stream.");
    }

    [Fact]
    public void TransformFrameHeader_RejectsCorruptOrTruncatedFrames()
    {
        var valid = new DeltaTransform(4).Encode([1, 2, 3, 4, 5, 6, 7, 8]);

        // Truncated header
        Assert.Throws<InvalidDataException>(() => TransformPipeline.DecodeFramed(valid.AsSpan(0, 8)));

        // Truncated payload
        Assert.Throws<InvalidDataException>(() => TransformPipeline.DecodeFramed(valid.AsSpan(0, valid.Length - 1)));

        // Wrong magic
        var badMagic = valid.ToArray();
        badMagic[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => TransformPipeline.DecodeFramed(badMagic));

        // Wrong transform kind assertion
        Assert.Throws<InvalidDataException>(() => new ByteShuffleTransform(4).Decode(valid));
    }

    internal static byte[] BuildSyntheticX86ExecutableSection(int sizeBytes)
    {
        var buf = new byte[sizeBytes];
        // Realistic x86-64 prologue/body filler + frequent CALL rel32 to 8 hot target addresses
        int[] hotTargets = [0x0080, 0x0140, 0x02A0, 0x0500, 0x09C0, 0x1200, 0x2400, 0x4800];
        ReadOnlySpan<byte> prologue = [0x55, 0x48, 0x89, 0xE5, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x8B, 0x45, 0xF8];

        var pos = 0;
        var callIndex = 0;
        while (pos < sizeBytes)
        {
            if (pos + 17 <= sizeBytes)
            {
                // Varying local register/stack offset so raw bytes aren't trivially identical, followed by CALL rel32
                prologue.CopyTo(buf.AsSpan(pos));
                buf[pos + 7] = (byte)(0x10 + ((callIndex * 7) & 0x70));
                buf[pos + 11] = (byte)(0xE0 + (callIndex * 3) % 24);
                pos += 12;

                var target = hotTargets[callIndex % hotTargets.Length];
                var ip = pos + 5;
                var rel32 = target - ip;
                buf[pos] = (callIndex % 5 == 0) ? (byte)0xE9 : (byte)0xE8;
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos + 1, 4), rel32);
                pos += 5;
                callIndex++;
            }
            else
            {
                buf[pos++] = 0x90; // NOP
            }
        }

        return buf;
    }

    private static byte[] CreateDeterministicBytes(int length, int seed)
    {
        var rng = new Random(seed);
        var data = new byte[length];
        rng.NextBytes(data);
        return data;
    }
}
