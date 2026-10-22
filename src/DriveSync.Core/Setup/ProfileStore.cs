using System.Text.Json;
using System.Text.Json.Serialization;
using DriveSync.Core.Model;

namespace DriveSync.Core;

/// <summary>Saves the backup profile on this PC, by default in <c>%APPDATA%\DriveSync\profile.json</c>.</summary>
public sealed class ProfileStore(string? filePath = null)
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FilePath { get; } = filePath
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DriveSync", "profile.json");

    /// <summary>Where the PC's copy of the catalog is kept.</summary>
    public string MirrorDirectory => Path.GetDirectoryName(FilePath)!;

    public BackupProfile? Load()
    {
        if (!File.Exists(FilePath))
            return null;
        return JsonSerializer.Deserialize<BackupProfile>(File.ReadAllText(FilePath), Json);
    }

    public void Save(BackupProfile profile)
    {
        Directory.CreateDirectory(MirrorDirectory);
        string temp = FilePath + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(profile, Json));
        File.Move(temp, FilePath, overwrite: true);
    }
}
