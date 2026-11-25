namespace UltraCompress.Core.Jobs;

/// <summary>
/// Cooperative pause token checked at chunk and block boundaries so jobs pause cleanly
/// without burning CPU and wake immediately upon resume or cancellation.
/// </summary>
public interface IPauseToken
{
    bool IsPaused { get; }

    ValueTask WaitWhilePausedAsync(CancellationToken cancellationToken = default);
}

public sealed class CooperativePauseController : IPauseToken
{
    private readonly object _gate = new();
    private TaskCompletionSource<bool>? _resumeTcs;

    public CooperativePauseController(bool startPaused = false)
    {
        if (startPaused)
        {
            _resumeTcs = CreateTcs();
        }
    }

    public event Action<bool>? PauseStateChanged;

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _resumeTcs is not null;
            }
        }
    }

    public bool Pause()
    {
        lock (_gate)
        {
            if (_resumeTcs is not null)
            {
                return false;
            }

            _resumeTcs = CreateTcs();
        }

        PauseStateChanged?.Invoke(true);
        return true;
    }

    public bool Resume()
    {
        TaskCompletionSource<bool>? toRelease;
        lock (_gate)
        {
            if (_resumeTcs is null)
            {
                return false;
            }

            toRelease = _resumeTcs;
            _resumeTcs = null;
        }

        toRelease.TrySetResult(true);
        PauseStateChanged?.Invoke(false);
        return true;
    }

    public ValueTask WaitWhilePausedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task? waitTask;
        lock (_gate)
        {
            waitTask = _resumeTcs?.Task;
        }

        if (waitTask is null || waitTask.IsCompleted)
        {
            return ValueTask.CompletedTask;
        }

        return new ValueTask(waitTask.WaitAsync(cancellationToken));
    }

    private static TaskCompletionSource<bool> CreateTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class CompositePauseToken : IPauseToken
{
    private readonly IPauseToken[] _tokens;

    public CompositePauseToken(params IPauseToken?[] tokens)
    {
        _tokens = tokens.Where(t => t is not null).Cast<IPauseToken>().ToArray();
    }

    public bool IsPaused => _tokens.Any(t => t.IsPaused);

    public async ValueTask WaitWhilePausedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var token in _tokens)
        {
            await token.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
