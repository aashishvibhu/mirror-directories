namespace DriveSync.Core;

/// <summary>An error whose message is written for the person using the app, ready to show as is.</summary>
public sealed class DriveSyncException(string message, Exception? inner = null) : Exception(message, inner);
