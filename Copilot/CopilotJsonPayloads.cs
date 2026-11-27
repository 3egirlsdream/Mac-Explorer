using MacExplorer.Models;
using System.Text.Json.Serialization;

namespace MacExplorer.Copilot;

internal sealed record PluginCommandReceipt(IReadOnlyList<string> Outputs, IReadOnlyList<string> Warnings);
internal sealed record DirectoryEntrySummary(string FullPath, string Name, bool IsDirectory, long Size,
    DateTime LastModified);
internal sealed record FileAnalysisTag(string TagType, string TagValue, float Confidence);
internal sealed record FileInformation(string FullPath, string Name, bool IsDirectory, long Size, DateTime Created,
    DateTime LastModified, DateTime? LastAccessed, string? Kind, string? ContentType, List<string>? Tags,
    FileImageInformation? Image, FileAnalysisTag[]? Analysis);
internal sealed record FileImageInformation(int PixelWidth, int PixelHeight, string ColorSpace, string CameraMake,
    string CameraModel, string LensModel, string FocalLength, string Aperture, string ExposureTime, string IsoSpeed,
    string WhiteBalance, string Flash, string ExposureProgram, string MeteringMode, DateTime? PhotoTakenDate,
    double Latitude, double Longitude, string Altitude);
internal sealed record IndexedSearchFields(IReadOnlyList<string> AnalysisTagTypes, string[] Filters,
    string[] Sources, string DateRange);
internal sealed record IndexedSearchCoverage(string Scope, int Offset, int? NextOffset, bool HasMore,
    long AnalyzedFiles, IReadOnlyList<string> IndexStatus, string Content);
internal sealed record FileEntrySummary(string FullPath, string Name, bool IsDirectory, long Size);
internal sealed record PaneSearchResult(string CurrentPath, string StatusText);
internal sealed record PluginCatalogEntry(string Id, string Name, bool Enabled, string Status, string? LastError,
    PluginCommandSummary[] Commands);
internal sealed record PluginCommandSummary(string Id, string Title);
internal sealed record RemoteServerSummary(string Id, string DisplayName, bool IsConnected);
internal sealed record RemoteConnectionResult(string Id, string DisplayName, string CurrentPath);
internal sealed record ScriptSummary(string Id, string Name);
internal sealed record SettingsSummary(string Theme, bool HideSystem, bool HideDotFiles, bool HideDotFolders,
    bool ConfirmTrash, bool DoubleClickUp, bool AiAnalysis, bool FolderCovers);
internal sealed record BatchRenameReceipt(List<BatchRenamePreviewItem> SuccessfulItems, List<string> Errors,
    List<string> Warnings, bool WasCancelled);
internal sealed record PluginSnapshot(string Id, string Version, bool Enabled, bool Running);
internal sealed record CapabilitySummary(string Id, string Name, string Description, CapabilityImpact Impact,
    string Confirmation);
internal sealed record ClassificationGroup([property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("paths")] string[] Paths);
internal sealed record ClassificationProposal([property: JsonPropertyName("candidateArtifactId")] string CandidateArtifactId,
    [property: JsonPropertyName("groups")] List<ClassificationGroup> Groups,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);
internal sealed record ClassificationProposalReceipt([property: JsonPropertyName("artifactId")] string ArtifactId,
    [property: JsonPropertyName("groupCount")] int GroupCount,
    [property: JsonPropertyName("fileCount")] int FileCount);
internal sealed record ApprovalRequiredDetails(string RequiredTool, string CapabilityId, bool ApprovalRequired);
internal sealed record CandidateFileSetReceipt([property: JsonPropertyName("artifactId")] string ArtifactId,
    [property: JsonPropertyName("result")] CapabilityResult Result);
internal sealed record OperationPlanSummary(string Id, string Summary, IReadOnlyList<string> AffectedPaths,
    string Confirmation);

internal readonly record struct RenamePreviewSnapshot(string OriginalPath, string NewPath, bool HasError, bool HasConflict);
internal sealed record AttachmentPromptEntry(string Path, string Type);
internal sealed record PaneLayoutArguments([property: JsonPropertyName("layout")] string Layout);
internal sealed record ViewModeArguments([property: JsonPropertyName("mode")] string Mode);
internal sealed record VisibilityArguments([property: JsonPropertyName("visible")] bool Visible);
internal sealed record SortArguments(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("ascending"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Ascending = null,
    [property: JsonPropertyName("priority"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Priority = null);
