namespace DriveSync.Core.Engine;

/// <summary>Lets the app pause a running sync. The sync stops after the file it's copying.</summary>
public sealed class SyncControl
{
    TaskCompletionSource? _resume;

    public bool IsPaused => Volatile.Read(ref _resume) is not null;

    public void Pause() => Interlocked.CompareExchange(ref _resume, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), null);

    public void Resume() => Interlocked.Exchange(ref _resume, null)?.TrySetResult();

    internal Task WaitIfPausedAsync(CancellationToken ct) => Volatile.Read(ref _resume)?.Task.WaitAsync(ct) ?? Task.CompletedTask;
}
