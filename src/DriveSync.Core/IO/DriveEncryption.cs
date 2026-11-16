namespace DriveSync.Core.IO;

public enum EncryptionState
{
    /// <summary>Windows didn't say: a network folder, an older Windows, or the question failed.</summary>
    Unknown,
    NotEncrypted,
    Encrypted,
}

/// <summary>
/// Whether a drive is protected by BitLocker. DriveSync stores plain copies so they can be opened without it;
/// encrypting the drive itself is how they're kept private if the drive is lost.
/// </summary>
public static class DriveEncryption
{
    public static EncryptionState Check(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
                return EncryptionState.Unknown;
            // The same value File Explorer uses for the padlock on a drive's icon.
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
                return EncryptionState.Unknown;
            dynamic shell = Activator.CreateInstance(shellType)!;
            object? value = shell.NameSpace(root)?.Self?.ExtendedProperty("System.Volume.BitLockerProtection");
            return value is null ? EncryptionState.Unknown : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) switch
            {
                1 or 3 or 5 or 6 => EncryptionState.Encrypted, // on, being encrypted, suspended, locked
                2 or 4 => EncryptionState.NotEncrypted,        // off, being decrypted
                _ => EncryptionState.Unknown,
            };
        }
        catch (Exception)
        {
            // A COM failure here must never get in the way of a backup.
            return EncryptionState.Unknown;
        }
    }
}
