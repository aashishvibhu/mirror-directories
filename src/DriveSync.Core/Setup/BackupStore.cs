using System.Text.Json;
using System.Text.Json.Serialization;
using DriveSync.Core.Model;

namespace DriveSync.Core;

/// <summary>
/// The list of backups set up on this PC, by default in <c>%APPDATA%\DriveSync\backups.json</c>.
/// The same folder holds a copy of each backup's catalog and the list of offloaded files.
/// </summary>
public sealed class BackupStore(string? folder = null)
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Folder { get; } = folder
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DriveSync");

    public string FilePath => Path.Combine(Folder, "backups.json");

    /// <summary>Where each backup's catalog is copied on the PC.</summary>
    public string MirrorDirectory => Folder;

    /// <summary>Files offloaded through DriveSync, shared by every backup.</summary>
    public string OffloadListPath => Path.Combine(Folder, "offloaded.db");

    /// <summary>The settings file of version 2.0's first builds, which held a single backup.</summary>
    string LegacyProfilePath => Path.Combine(Folder, "profile.json");

    /// <summary>
    /// Reads the backups. An older single-backup <c>profile.json</c> becomes the first backup, named "My backup";
    /// the old file is left where it is.
    /// </summary>
    public List<BackupProfile> Load()
    {
        if (File.Exists(FilePath))
            return JsonSerializer.Deserialize<List<BackupProfile>>(File.ReadAllText(FilePath), Json) ?? [];
        if (File.Exists(LegacyProfilePath) && JsonSerializer.Deserialize<BackupProfile>(File.ReadAllText(LegacyProfilePath), Json) is { } single)
        {
            List<BackupProfile> migrated = [single];
            Save(migrated);
            return migrated;
        }
        return [];
    }

    public void Save(IEnumerable<BackupProfile> backups)
    {
        Directory.CreateDirectory(Folder);
        string temp = FilePath + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(backups.ToList(), Json));
        File.Move(temp, FilePath, overwrite: true);
    }
}
