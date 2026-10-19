namespace MacExplorer.Models;

public sealed record FileTaskRequest(BackgroundTaskKind Kind, string Label,
    IReadOnlyList<string> Sources, string? Destination = null, string? DisplayName = null);

public enum FileTaskItemState { Completed, Failed, Skipped, NotAttempted }

public sealed record FileTaskItemResult(string Source, string? Destination,
    FileTaskItemState State, string? Error = null);
