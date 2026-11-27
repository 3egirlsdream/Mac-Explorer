using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Search;

namespace MacExplorer.Copilot;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AppCapability))]
[JsonSerializable(typeof(ApprovalRequiredDetails))]
[JsonSerializable(typeof(AttachmentPromptEntry[]))]
[JsonSerializable(typeof(BatchRenameReceipt))]
[JsonSerializable(typeof(CandidateFileSetReceipt))]
[JsonSerializable(typeof(CapabilityPlan))]
[JsonSerializable(typeof(CapabilityResult))]
[JsonSerializable(typeof(CapabilitySummary[]))]
[JsonSerializable(typeof(ClassificationProposal))]
[JsonSerializable(typeof(ClassificationProposalReceipt))]
[JsonSerializable(typeof(CopilotAttachment[]))]
[JsonSerializable(typeof(CopilotContentExtractor.Page))]
[JsonSerializable(typeof(CopilotTraceStep))]
[JsonSerializable(typeof(DirectoryEntrySummary))]
[JsonSerializable(typeof(FileDeliveryEntry[]))]
[JsonSerializable(typeof(FileEntrySummary[]))]
[JsonSerializable(typeof(FileInformation))]
[JsonSerializable(typeof(FileOperationResult))]
[JsonSerializable(typeof(FileTag[]))]
[JsonSerializable(typeof(IndexedFileMatch[]))]
[JsonSerializable(typeof(IndexedSearchCoverage))]
[JsonSerializable(typeof(IndexedSearchFields))]
[JsonSerializable(typeof(IEnumerable<string>))]
[JsonSerializable(typeof(IReadOnlyList<CopilotAttachment>))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(List<FileEntrySummary>))]
[JsonSerializable(typeof(List<FileTag>))]
[JsonSerializable(typeof(List<PinnedFolder>))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(OperationPlanSummary))]
[JsonSerializable(typeof(PaneLayoutArguments))]
[JsonSerializable(typeof(PaneSearchResult))]
[JsonSerializable(typeof(PinnedFolder[]))]
[JsonSerializable(typeof(PluginCatalogEntry[]))]
[JsonSerializable(typeof(PluginCommandReceipt))]
[JsonSerializable(typeof(PluginSnapshot[]))]
[JsonSerializable(typeof(RemoteConnectionResult))]
[JsonSerializable(typeof(RemoteServerSummary[]))]
[JsonSerializable(typeof(ScriptSummary[]))]
[JsonSerializable(typeof(SettingsSummary))]
[JsonSerializable(typeof(SortArguments))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(ViewModeArguments))]
[JsonSerializable(typeof(VisibilityArguments))]
internal sealed partial class CopilotJsonContext : JsonSerializerContext
{
    internal static CopilotJsonContext Relaxed { get; } = new(new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(BatchRenameOptions))]
[JsonSerializable(typeof(BatchRenameRule))]
[JsonSerializable(typeof(IndexedFileQuery))]
internal sealed partial class CopilotInputJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(string))]
internal sealed partial class CopilotToolJsonContext : JsonSerializerContext
{
    internal static CopilotToolJsonContext ToolContracts { get; } = new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
}
