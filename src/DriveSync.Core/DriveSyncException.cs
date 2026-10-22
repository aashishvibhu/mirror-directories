namespace DriveSync.Core;

/// <summary>An error whose message is written for the person using the app, ready to show as is.</summary>
public class DriveSyncException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Another DriveSync task is using the backup drive right now. Trying again shortly usually works.</summary>
public sealed class DriveBusyException(string message, Exception? inner = null) : DriveSyncException(message, inner);
