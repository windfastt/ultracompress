using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UltraCompress.Core.Ipc;

/// <summary>Newline-delimited JSON over a bidirectional stream (one object per line).</summary>
public static class EngineJsonProtocol
{
    public static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        opts.Converters.Add(new JsonStringEnumConverter());
        return opts;
    }

    public static async Task WriteRequestAsync(Stream stream, EngineRequest request, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(request, SerializerOptions) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<EngineResponse?> ReadResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        return JsonSerializer.Deserialize<EngineResponse>(line, SerializerOptions);
    }
}
