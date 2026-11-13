namespace MacExplorer.Models;

public enum RemoteEditState { Synced, Pending, Uploading, Conflict, Failed, Stopped }
public enum RemoteEditFailureReason { Unknown, ConnectionRefused, ConnectionLost, TimedOut, PermissionDenied }
public sealed record RemoteEditSession(string LocalPath, string RemotePath, string ServerId,
    RemoteEditState State, string? Error, bool HasPendingChanges)
{
    public RemoteEditFailureReason FailureReason { get; init; }
}
