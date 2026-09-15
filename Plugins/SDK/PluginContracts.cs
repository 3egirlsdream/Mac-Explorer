using System.Text.Json;
using System.Text.Json.Serialization;

namespace MacExplorer.PluginSdk;

public static class PluginProtocol
{
    public const int ApiVersion = 2;
    public const string WorkerArgument = "--plugin-worker";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed record PluginManifest
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public int ApiVersion { get; init; }
    public string Entry { get; init; } = "";
    public string Icon { get; init; } = "convert";
    public string Description { get; init; } = "";
    public string Developer { get; init; } = "";
    public string Platform { get; init; } = "osx";
    public string Architecture { get; init; } = "arm64";
    public bool Paid { get; init; }
    public int TrialDays { get; init; }
    public bool HasUserInterface { get; init; }
    public PluginCommand[] Commands { get; init; } = [];
}

public sealed record PluginCommand
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Icon { get; init; } = "convert";
    public PluginMatch Match { get; init; } = new();
}

public sealed record PluginMatch
{
    public string[] Extensions { get; init; } = [];
    public string[] FileNames { get; init; } = [];
    public bool TextFiles { get; init; }
    public int MinSelection { get; init; } = 1;
    public int MaxSelection { get; init; } = 1;

    public bool Matches(IReadOnlyList<PluginFile> files)
        => files.Count >= MinSelection && files.Count <= MaxSelection && files.All(file =>
            file.Source == "local" && !file.IsDirectory && Path.IsPathFullyQualified(file.Path) &&
            (Extensions.Contains(Path.GetExtension(file.Path), StringComparer.OrdinalIgnoreCase) ||
             FileNames.Contains(Path.GetFileName(file.Path), StringComparer.OrdinalIgnoreCase) ||
             (TextFiles && Services.TextFileTypes.IsText(file.Path))));
}

public sealed record PluginFile(string Path, string Source = "local", bool IsDirectory = false);
public sealed record PluginInvocation(string InvocationId, string CommandId, PluginFile[] Files,
    string WorkDirectory, Dictionary<string, JsonElement>? Parameters = null);
public sealed record PluginConfiguration(string Kind, string Title, int Width, int Height, string Format);
public sealed record PluginPreparation(PluginConfiguration? Configuration = null);
public sealed record PluginOutput(string Path, string SuggestedName);
public sealed record PluginResult(PluginOutput[] Outputs, string[] Warnings);
public sealed record PluginProgress(string Message, double? Percent = null);

public interface IFileActionPlugin
{
    Task<PluginPreparation> PrepareAsync(PluginInvocation invocation, CancellationToken cancellationToken);
    Task<PluginResult> ExecuteAsync(PluginInvocation invocation, IProgress<PluginProgress> progress,
        CancellationToken cancellationToken);
}

public sealed record PluginRpcError(int Code, string Message, string? Data = null);
public sealed record PluginRpcMessage
{
    public string Jsonrpc { get; init; } = "2.0";
    public int? Id { get; init; }
    public string? Method { get; init; }
    public JsonElement? Params { get; init; }
    public JsonElement? Result { get; init; }
    public PluginRpcError? Error { get; init; }
}

public enum PluginAccessStatus { Allowed, LoginRequired, PurchaseRequired, TrialAvailable, TrialExpired, Failed }
public sealed record PluginTrial(DateTimeOffset? StartedAt = null, DateTimeOffset? ExpiresAt = null, DateTimeOffset? CheckedAt = null)
{
    public bool Active => ExpiresAt is { } end && CheckedAt is { } now && now < end;
}
public sealed record PluginAccessRequest(PluginInvocation Invocation, PluginTrial Trial);
public sealed record PluginAccessResult(PluginAccessStatus Status, string Message = "");
public sealed record PluginInteractionRequest(PluginAccessRequest Access, string Purpose);
public sealed record PluginInteractionResult(bool Completed);
public interface IPluginAccessProvider
{
    Task<PluginAccessResult> CheckAccessAsync(PluginAccessRequest request, CancellationToken cancellationToken);
    Task<PluginInteractionResult> ShowAccountAsync(PluginInteractionRequest request, CancellationToken cancellationToken);
}
