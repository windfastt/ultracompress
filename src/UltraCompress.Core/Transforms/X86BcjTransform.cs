using System.Buffers.Binary;

namespace UltraCompress.Core.Transforms;

/// <summary>
/// 100% bijective x86/x86-64 Branch-Call-Jump (BCJ) filter converting near relative CALL (0xE8) and JMP (0xE9)
/// 32-bit displacements into absolute target addresses (modulo 2^25 on sign-extended ±16 MiB displacements)
/// for PE (.exe/.dll) and ELF native binaries.
/// </summary>
public sealed class X86BcjTransform : ILosslessTransform
{
    public static readonly X86BcjTransform Instance = new();

    private const byte CallRel32Opcode = 0xE8;
    private const byte JmpRel32Opcode = 0xE9;
    private const uint Mask25Bit = 0x01FF_FFFFu;
    private const uint SignBit25 = 0x0100_0000u;

    public TransformKind Kind => TransformKind.X86Bcj;

    public string Name => "x86_bcj";

    public byte[] Encode(ReadOnlySpan<byte> input)
    {
        var output = new byte[TransformFrameHeader.HeaderSizeBytes + input.Length];
        var header = new TransformFrameHeader(TransformKind.X86Bcj, Parameter: 0, OriginalLength: input.Length);
        header.WriteTo(output);

        if (input.IsEmpty)
        {
            return output;
        }

        var payload = output.AsSpan(TransformFrameHeader.HeaderSizeBytes, input.Length);
        var limit = input.Length - 5;
        var i = 0;

        while (i <= limit)
        {
            var opcode = input[i];
            if (opcode is CallRel32Opcode or JmpRel32Opcode)
            {
                payload[i] = opcode;
                var msb = input[i + 4];
                if (msb is 0x00 or 0xFF)
                {
                    var rel = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(i + 1, 4));
                    var v25 = (rel & 0x00FF_FFFFu) | (((uint)msb & 1u) << 24);
                    var ip = (uint)(i + 5);
                    var abs25 = unchecked(v25 + ip) & Mask25Bit;
                    var outMsb = (abs25 & SignBit25) != 0 ? (byte)0xFF : (byte)0x00;
                    var encoded = (abs25 & 0x00FF_FFFFu) | ((uint)outMsb << 24);
                    BinaryPrimitives.WriteUInt32LittleEndian(payload.Slice(i + 1, 4), encoded);
                }
                else
                {
                    input.Slice(i + 1, 4).CopyTo(payload.Slice(i + 1, 4));
                }

                i += 5;
            }
            else
            {
                payload[i] = opcode;
                i++;
            }
        }

        while (i < input.Length)
        {
            payload[i] = input[i];
            i++;
        }

        return output;
    }

    public byte[] Decode(ReadOnlySpan<byte> encodedFrame)
    {
        var header = TransformFrameHeader.ReadAndValidate(encodedFrame, TransformKind.X86Bcj);
        var length = checked((int)header.OriginalLength);
        if (length == 0)
        {
            return Array.Empty<byte>();
        }

        var payload = encodedFrame.Slice(TransformFrameHeader.HeaderSizeBytes, length);
        var output = new byte[length];
        var limit = length - 5;
        var i = 0;

        while (i <= limit)
        {
            var opcode = payload[i];
            if (opcode is CallRel32Opcode or JmpRel32Opcode)
            {
                output[i] = opcode;
                var msb = payload[i + 4];
                if (msb is 0x00 or 0xFF)
                {
                    var abs = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(i + 1, 4));
                    var abs25 = (abs & 0x00FF_FFFFu) | (((uint)msb & 1u) << 24);
                    var ip = (uint)(i + 5);
                    var rel25 = unchecked(abs25 - ip) & Mask25Bit;
                    var origMsb = (rel25 & SignBit25) != 0 ? (byte)0xFF : (byte)0x00;
                    var decoded = (rel25 & 0x00FF_FFFFu) | ((uint)origMsb << 24);
                    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(i + 1, 4), decoded);
                }
                else
                {
                    payload.Slice(i + 1, 4).CopyTo(output.AsSpan(i + 1, 4));
                }

                i += 5;
            }
            else
            {
                output[i] = opcode;
                i++;
            }
        }

        while (i < length)
        {
            output[i] = payload[i];
            i++;
        }

        return output;
    }
}
