namespace DriveSync.Core.Storage;

/// <summary>Keeps two DriveSync tasks from working on the same backup at once.</summary>
public sealed class RunLock : IDisposable
{
    readonly FileStream _handle;

    RunLock(FileStream handle) => _handle = handle;

    public static RunLock Acquire(string path)
    {
        try
        {
            return new RunLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException e)
        {
            throw new DriveBusyException("Another DriveSync task is already using this backup drive. Wait for it to finish, then try again.", e);
        }
    }

    public void Dispose() => _handle.Dispose();
}
