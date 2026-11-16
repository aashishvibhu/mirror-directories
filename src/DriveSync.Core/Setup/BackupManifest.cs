using System.Text.Json;
using System.Text.Json.Serialization;
using DriveSync.Core.Model;
using DriveSync.Core.Storage;

namespace DriveSync.Core;

/// <summary>
/// A backup's settings, kept on its own drive in <c>.drivesync\backup.json</c>. The list of backups lives on one PC;
/// this copy lets the drive be used from another PC, or reconnected after the app forgot it, with the same folders.
/// </summary>
public sealed class BackupManifest
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Name { get; set; } = "";

    public List<SourceFolder> Sources { get; set; } = [];

    public List<RemovedFolder> RemovedFolders { get; set; } = [];

    public List<string> Exclusions { get; set; } = [];

    public MissingFilePolicy MissingFilePolicy { get; set; }

    public RemovedFilesGoTo RemovedFilesGoTo { get; set; }

    /// <summary>The PC that last used this backup, to tell "this PC again" from "a new PC".</summary>
    public string PcName { get; set; } = "";

    public static string PathFor(string backupRoot) => Path.Combine(backupRoot, BackupLocation.MetaFolderName, "backup.json");

    public static BackupManifest From(BackupProfile profile) => new()
    {
        Name = profile.Name,
        Sources = [.. profile.Sources],
        RemovedFolders = [.. profile.RemovedFolders],
        Exclusions = [.. profile.Exclusions],
        MissingFilePolicy = profile.MissingFilePolicy,
        RemovedFilesGoTo = profile.RemovedFilesGoTo,
        PcName = Environment.MachineName,
    };

    /// <summary>Saves the settings on the drive if they changed. A drive that can't be written to is left alone.</summary>
    public static void TryWrite(string backupRoot, BackupProfile profile)
    {
        try
        {
            string path = PathFor(backupRoot), text = JsonSerializer.Serialize(From(profile), Json);
            if (File.Exists(path) && File.ReadAllText(path) == text)
                return;
            string temp = path + ".new";
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static BackupManifest? TryRead(string backupRoot)
    {
        try
        {
            string path = PathFor(backupRoot);
            return File.Exists(path) ? JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
