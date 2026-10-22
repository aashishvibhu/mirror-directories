using DriveSync.Core;
using DriveSync.Core.Engine;
using DriveSync.Core.Model;
using DriveSync.Core.RecycleBin;

namespace DriveSync.App.Services;

/// <summary>What the app shares between screens: the saved profile, the Recycle Bin, and opening the backup drive.</summary>
public sealed class AppServices
{
    AppServices(ProfileStore profiles, IRecycleBin recycleBin, bool usesStandInRecycleBin)
    {
        Profiles = profiles;
        RecycleBin = recycleBin;
        UsesStandInRecycleBin = usesStandInRecycleBin;
    }

    public ProfileStore Profiles { get; }

    public IRecycleBin RecycleBin { get; }

    /// <summary>True in demo and test runs, where a folder stands in for the Recycle Bin.</summary>
    public bool UsesStandInRecycleBin { get; }

    public BackupProfile? Profile { get; private set; }

    /// <summary>
    /// Normal runs keep settings in <c>%APPDATA%\DriveSync</c> and use the real Recycle Bin.
    /// DRIVESYNC_DATA_DIR and DRIVESYNC_RECYCLE_BIN_DIR move both somewhere else, for demos and tests.
    /// </summary>
    public static AppServices FromEnvironment()
    {
        string? dataDir = Environment.GetEnvironmentVariable("DRIVESYNC_DATA_DIR");
        string? binDir = Environment.GetEnvironmentVariable("DRIVESYNC_RECYCLE_BIN_DIR");
        var profiles = new ProfileStore(string.IsNullOrEmpty(dataDir) ? null : Path.Combine(dataDir, "profile.json"));
        return string.IsNullOrEmpty(binDir)
            ? new AppServices(profiles, new WindowsRecycleBin(), usesStandInRecycleBin: false)
            : new AppServices(profiles, new FolderRecycleBin(binDir), usesStandInRecycleBin: true);
    }

    public BackupProfile? LoadProfile() => Profile = Profiles.Load();

    public void SaveProfile(BackupProfile profile)
    {
        Profiles.Save(profile);
        Profile = profile;
    }

    /// <summary>Opens the backup drive. Throws <see cref="DriveSyncException"/> if it isn't connected or is busy.</summary>
    public DriveSyncSession OpenSession()
    {
        var profile = Profile ?? throw new DriveSyncException("Set up DriveSync first.");
        string? before = profile.LastKnownRoot;
        var session = DriveSyncSession.Open(profile, RecycleBin, Profiles.MirrorDirectory);
        if (!string.Equals(before, profile.LastKnownRoot, StringComparison.OrdinalIgnoreCase))
            Profiles.Save(profile); // the drive came back under a different letter
        return session;
    }

    /// <summary>
    /// Opens the backup drive on a background thread. Screens open it briefly to read or change a few records,
    /// so if another of those quick tasks holds it, this waits a few seconds before giving up.
    /// </summary>
    public async Task<DriveSyncSession> OpenSessionAsync(CancellationToken ct = default)
    {
        var giveUp = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            try
            {
                return await Task.Run(OpenSession, ct);
            }
            catch (DriveBusyException) when (DateTime.UtcNow < giveUp)
            {
                await Task.Delay(150, ct);
            }
        }
    }

    /// <summary>The PC's copy of the catalog, used to show counts while the drive is unplugged.</summary>
    public string? MirrorCatalogPath =>
        Profile is null ? null : Path.Combine(Profiles.MirrorDirectory, $"catalog-{Profile.DriveId}.db");
}
