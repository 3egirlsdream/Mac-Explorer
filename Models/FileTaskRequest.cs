namespace MacExplorer.Models;

public sealed record FileTaskRequest(BackgroundTaskKind Kind, string Label,
    IReadOnlyList<string> Sources, string? Destination = null, string? DisplayName = null)
{
    internal FileOperationDisplay? Operation { get; init; }
    internal Services.Subscriptions.PremiumFeature RequiredFeatures { get; init; }
}

public enum FileTaskItemState { Completed, Failed, Skipped, NotAttempted }

public sealed record FileTaskItemResult(string Source, string? Destination,
    FileTaskItemState State, string? Error = null)
{
    public string StateLabel => Services.Impl.LocalizationText.Get("operation.item." + State);
    public string Detail => string.Join(Environment.NewLine, new[] { Source, Destination, Error }.Where(s => !string.IsNullOrEmpty(s)));
}
