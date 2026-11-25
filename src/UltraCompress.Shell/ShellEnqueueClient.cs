using System.Text.Json;
using UltraCompress.Core;

namespace UltraCompress.Shell;

/// <summary>
/// Phase 4 stub: Explorer invokes native shell, which calls this client to drop jobs for the engine.
/// </summary>
public sealed class ShellEnqueueClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly string _queueDirectory;

    public ShellEnqueueClient(string? queueDirectory = null)
    {
        _queueDirectory = queueDirectory ?? ShellConstants.DefaultQueueDirectory;
    }

    public Task EnqueueAsync(
        ShellEnqueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Paths.Count == 0)
        {
            throw new ArgumentException("At least one path is required.", nameof(request));
        }

        Directory.CreateDirectory(_queueDirectory);
        var payload = new QueueLine(
            request.Paths,
            request.Profile.ToString(),
            request.RequestedAtUtc);

        var line = JsonSerializer.Serialize(payload, JsonOptions);
        var inbox = Path.Combine(_queueDirectory, ShellConstants.DefaultQueueFileName);
        return File.AppendAllTextAsync(inbox, line + Environment.NewLine, cancellationToken);
    }

    private sealed record QueueLine(
        IReadOnlyList<string> Paths,
        string Profile,
        DateTimeOffset RequestedAtUtc);
}