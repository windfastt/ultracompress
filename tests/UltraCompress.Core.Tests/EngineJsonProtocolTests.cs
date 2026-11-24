using UltraCompress.Core.Ipc;

namespace UltraCompress.Core.Tests;

public sealed class EngineJsonProtocolTests
{
    [Fact]
    public async Task WriteRequest_serializes_ping_line()
    {
        await using var stream = new MemoryStream();
        await EngineJsonProtocol.WriteRequestAsync(
            stream,
            new EngineRequest { Type = EngineMessageTypes.Ping, Id = "test" },
            CancellationToken.None);

        stream.Position = 0;
        var line = await new StreamReader(stream).ReadLineAsync();
        Assert.Contains("ping", line, StringComparison.Ordinal);
        Assert.Contains("test", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadResponse_parses_status()
    {
        var response = new EngineResponse { Type = EngineMessageTypes.Status, ActiveJobs = 0, QueuedJobs = 2 };
        var line = System.Text.Json.JsonSerializer.Serialize(response, EngineJsonProtocol.SerializerOptions) + "\n";
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(line));
        var read = await EngineJsonProtocol.ReadResponseAsync(stream, CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(EngineMessageTypes.Status, read!.Type);
        Assert.Equal(2, read.QueuedJobs);
    }
}