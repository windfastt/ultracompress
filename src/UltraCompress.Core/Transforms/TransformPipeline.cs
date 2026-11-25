using System.Globalization;
using System.Text.Json;

namespace UltraCompress.Core.Transforms;

/// <summary>
/// Resolves and executes ordered <see cref="PreprocessStep"/> chains and their exact inverse decodes.
/// </summary>
public static class TransformPipeline
{
    public static bool IsIdentityStep(PreprocessStep step) =>
        step.Name.Equals("raw", StringComparison.OrdinalIgnoreCase)
        || step.Name.Equals("noop", StringComparison.OrdinalIgnoreCase);

    public static ILosslessTransform Resolve(PreprocessStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var normalized = step.Name.Trim().ToLowerInvariant();

        return normalized switch
        {
            "raw" or "noop" => RawTransform.Instance,
            "delta" => new DeltaTransform(ReadIntParam(step.Params, "stride", defaultValue: 4)),
            "delta1" => new DeltaTransform(1),
            "delta2" => new DeltaTransform(2),
            "delta4" => new DeltaTransform(4),
            "delta8" => new DeltaTransform(8),
            "byteshuffle" => new ByteShuffleTransform(ReadIntParam(step.Params, "stride", defaultValue: 4)),
            "byteshuffle4" => new ByteShuffleTransform(4),
            "byteshuffle8" => new ByteShuffleTransform(8),
            "x86_bcj" or "bcj_x86" => X86BcjTransform.Instance,
            _ => throw new NotSupportedException($"Unsupported lossless preprocessor '{step.Name}'."),
        };
    }

    public static byte[] Encode(ReadOnlySpan<byte> input, IReadOnlyList<PipelineStep> steps)
    {
        byte[]? current = null;
        foreach (var step in steps.OfType<PreprocessStep>())
        {
            if (IsIdentityStep(step))
            {
                continue;
            }

            var transform = Resolve(step);
            current = transform.Encode(current is null ? input : current.AsSpan());
        }

        return current ?? input.ToArray();
    }

    public static byte[] Decode(ReadOnlySpan<byte> transformed, IReadOnlyList<PipelineStep> steps)
    {
        var preprocessSteps = steps.OfType<PreprocessStep>().ToList();
        byte[]? current = null;

        for (var i = preprocessSteps.Count - 1; i >= 0; i--)
        {
            var step = preprocessSteps[i];
            var activeSpan = current is null ? transformed : current.AsSpan();

            if (IsIdentityStep(step))
            {
                // Transparently unwrap framed Raw if caller explicitly framed it; otherwise pass through.
                if (TransformFrameHeader.TryRead(activeSpan, out var header, out _)
                    && header.Kind == TransformKind.Raw
                    && activeSpan.Length - TransformFrameHeader.HeaderSizeBytes == header.OriginalLength)
                {
                    current = RawTransform.Instance.Decode(activeSpan);
                }

                continue;
            }

            var transform = Resolve(step);
            current = transform.Decode(activeSpan);
        }

        return current ?? transformed.ToArray();
    }

    /// <summary>
    /// Decodes any self-framed UCTF buffer by inspecting its <see cref="TransformFrameHeader"/>.
    /// </summary>
    public static byte[] DecodeFramed(ReadOnlySpan<byte> encodedFrame)
    {
        var header = TransformFrameHeader.ReadAndValidate(encodedFrame);
        ILosslessTransform transform = header.Kind switch
        {
            TransformKind.Raw => RawTransform.Instance,
            TransformKind.Delta => new DeltaTransform(header.Parameter),
            TransformKind.ByteShuffle => new ByteShuffleTransform(header.Parameter),
            TransformKind.X86Bcj => X86BcjTransform.Instance,
            _ => throw new InvalidDataException($"Unsupported transform kind '{header.Kind}'."),
        };

        return transform.Decode(encodedFrame);
    }

    public static int ReadIntParam(IReadOnlyDictionary<string, object?>? parameters, string key, int defaultValue)
    {
        if (parameters is null || !parameters.TryGetValue(key, out var rawValue) || rawValue is null)
        {
            return defaultValue;
        }

        return rawValue switch
        {
            int i => i,
            long l => checked((int)l),
            byte b => b,
            short s => s,
            JsonElement je when je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var ji) => ji,
            JsonElement je when je.ValueKind == JsonValueKind.String && int.TryParse(je.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var js) => js,
            string str when int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            IConvertible conv => conv.ToInt32(CultureInfo.InvariantCulture),
            _ => defaultValue,
        };
    }
}
