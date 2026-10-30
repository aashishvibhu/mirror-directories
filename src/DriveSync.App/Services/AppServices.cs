using DriveSync.Core;
using DriveSync.Core.Engine;
using DriveSync.Core.Model;
using DriveSync.Core.RecycleBin;
using DriveSync.Core.Storage;

namespace DriveSync.App.Services;

/// <summary>What the app shares between screens: the backups, the Recycle Bin, and opening a backup's drive.</summary>
public sealed class AppServices
{
    AppServices(BackupStore store, IRecycleBin recycleBin, bool usesStandInRecycleBin)
    {
        Store = store;
        RecycleBin = recycleBin;
        UsesStandInRecycleBin = usesStandInRecycleBin;
    }

    public BackupStore Store { get; }

    public IRecycleBin RecycleBin { get; }

    /// <summary>True in demo and test runs, where a folder stands in for the Recycle Bin.</summary>
    public bool UsesStandInRecycleBin { get; }

    /// <summary>Every backup set up on this PC, in the order they were added.</summary>
    public List<BackupProfile> Backups { get; } = [];

    /// <summary>
    /// Normal runs keep settings in <c>%APPDATA%\DriveSync</c> and use the real Recycle Bin.
    /// DRIVESYNC_DATA_DIR and DRIVESYNC_RECYCLE_BIN_DIR move both somewhere else, for demos and tests.
    /// </summary>
    public static AppServices FromEnvironment()
    {
        string? dataDir = Environment.GetEnvironmentVariable("DRIVESYNC_DATA_DIR");
        string? binDir = Environment.GetEnvironmentVariable("DRIVESYNC_RECYCLE_BIN_DIR");
        var store = new BackupStore(string.IsNullOrEmpty(dataDir) ? null : dataDir);
        return string.IsNullOrEmpty(binDir)
            ? new AppServices(store, new WindowsRecycleBin(), usesStandInRecycleBin: false)
            : new AppServices(store, new FolderRecycleBin(binDir), usesStandInRecycleBin: true);
    }

    /// <summary>For tests: settings in <paramref name="dataDir"/> and a given Recycle Bin.</summary>
    internal static AppServices Create(string dataDir, IRecycleBin recycleBin) =>
        new(new BackupStore(dataDir), recycleBin, usesStandInRecycleBin: true);

    public void Load()
    {
        Backups.Clear();
        Backups.AddRange(Store.Load());
    }

    public void Save() => Store.Save(Backups);

    public void Add(BackupProfile backup)
    {
        Backups.Add(backup);
        Save();
    }

    /// <summary>Forgets a backup. Nothing on its drive is touched.</summary>
    public void Remove(BackupProfile backup)
    {
        Backups.Remove(backup);
        Save();
    }

    /// <summary>Opens a backup's drive. Throws <see cref="DriveSyncException"/> if it isn't connected or is busy.</summary>
    public DriveSyncSession OpenSession(BackupProfile backup)
    {
        string? before = backup.LastKnownRoot;
        var session = DriveSyncSession.Open(backup, RecycleBin, Store.MirrorDirectory, Store.OffloadListPath);
        if (!string.Equals(before, backup.LastKnownRoot, StringComparison.OrdinalIgnoreCase))
            Save(); // the drive came back under a different letter
        return session;
    }

    /// <summary>
    /// Opens a backup's drive on a background thread. Screens open it briefly to read or change a few records,
    /// so if another of those quick tasks holds it, this waits a few seconds before giving up.
    /// </summary>
    public async Task<DriveSyncSession> OpenSessionAsync(BackupProfile backup, CancellationToken ct = default)
    {
        var giveUp = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            try
            {
                return await Task.Run(() => OpenSession(backup), ct);
            }
            catch (DriveBusyException) when (DateTime.UtcNow < giveUp)
            {
                await Task.Delay(150, ct);
            }
        }
    }

    /// <summary>The PC's copy of a backup's catalog, used to show counts while its drive is unplugged.</summary>
    public string MirrorCatalogPath(BackupProfile backup) => Path.Combine(Store.MirrorDirectory, $"catalog-{backup.DriveId}.db");

    public static bool IsConnected(BackupProfile backup) => BackupLocation.Find(backup) is not null;
}
