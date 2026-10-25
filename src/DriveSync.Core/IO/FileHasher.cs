using System.Security.Cryptography;

namespace DriveSync.Core.IO;

/// <summary>SHA-256 checksums, and copying a file while computing its checksum in the same pass.</summary>
public static class FileHasher
{
    public const int BufferSize = 1 << 20;

    /// <summary>Opens a file for reading while still letting other programs use it.</summary>
    public static FileStream OpenRead(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.ReadWrite | FileShare.Delete,
        BufferSize = 0,
        Options = FileOptions.SequentialScan,
    });

    public static string HashFile(string path, CancellationToken ct = default)
    {
        using var stream = OpenRead(path);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/> and returns the SHA-256 of what was read.
    /// Each block is written while the next one is read and hashed, so the file is read only once.
    /// The copy keeps the source's created and modified times.
    /// </summary>
    public static async Task<string> CopyWithHashAsync(string source, string destination, Action<long>? onBytes = null, CancellationToken ct = default)
    {
        string hash;
        await using (var input = OpenRead(source))
        await using (var output = new FileStream(destination, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 0,
            Options = FileOptions.Asynchronous,
            PreallocationSize = input.Length,
        }))
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] current = new byte[BufferSize], writing = new byte[BufferSize];
            ValueTask pendingWrite = ValueTask.CompletedTask;
            int read;
            while ((read = await input.ReadAsync(current.AsMemory(0, BufferSize), ct)) > 0)
            {
                sha.AppendData(current, 0, read);
                await pendingWrite;
                pendingWrite = output.WriteAsync(current.AsMemory(0, read), ct);
                (current, writing) = (writing, current);
                onBytes?.Invoke(read);
            }
            await pendingWrite;
            hash = Convert.ToHexString(sha.GetHashAndReset());
        }

        var info = new FileInfo(source);
        File.SetCreationTimeUtc(destination, info.CreationTimeUtc);
        File.SetLastWriteTimeUtc(destination, info.LastWriteTimeUtc);
        return hash;
    }
}
