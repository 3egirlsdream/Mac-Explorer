using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Platforms.MacOS;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Subscriptions;

namespace MacExplorer.Serialization;

[JsonSerializable(typeof(AppTrialRecord))]
[JsonSerializable(typeof(BackgroundThemeOptions))]
[JsonSerializable(typeof(BatchRenameRule))]
[JsonSerializable(typeof(ConversionPaths))]
[JsonSerializable(typeof(Dictionary<string, HomeFolderLayout>))]
[JsonSerializable(typeof(Dictionary<string, HomeScriptCommand[]>))]
[JsonSerializable(typeof(Dictionary<string, ShortcutBinding>))]
[JsonSerializable(typeof(Dictionary<string, SftpHostKey>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(DirectoryAccess.BookmarkStore))]
[JsonSerializable(typeof(DirectoryPreferences))]
[JsonSerializable(typeof(DiskInfo[]))]
[JsonSerializable(typeof(DocumentLocation))]
[JsonSerializable(typeof(FileDeliveryPreferences))]
[JsonSerializable(typeof(FileRecoveryRecord))]
[JsonSerializable(typeof(FolderTabSession))]
[JsonSerializable(typeof(FolderWindowSession))]
[JsonSerializable(typeof(HomeSectionPreferences))]
[JsonSerializable(typeof(ImageAnalysisResult))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(List<BatchRenamePreset>))]
[JsonSerializable(typeof(List<RemoteServerInfo>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<TabGroupPreset>))]
[JsonSerializable(typeof(MacNetworkNative.MountResult))]
[JsonSerializable(typeof(NetworkDiscoveryService.BonjourResult))]
[JsonSerializable(typeof(NetworkSmokeReport))]
[JsonSerializable(typeof(PdfAnalysisProgress))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(TabGroupPreset[]))]
[JsonSerializable(typeof(VersionInfo))]
[JsonSerializable(typeof(VolumeInfo[]))]
internal sealed partial class AppJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(WriteIndented = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(Dictionary<string, SftpHostKey>))]
[JsonSerializable(typeof(List<RemoteServerInfo>))]
internal sealed partial class IndentedAppJsonContext : JsonSerializerContext
{
    internal static JsonTypeInfo<List<RemoteServerInfo>> RemoteServers => RemoteServerContract.TypeInfo;

    private static class RemoteServerContract
    {
        internal static readonly JsonTypeInfo<List<RemoteServerInfo>> TypeInfo = CreateTypeInfo();

        private static JsonTypeInfo<List<RemoteServerInfo>> CreateTypeInfo()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                TypeInfoResolver = Default.WithAddedModifier(info =>
                {
                    if (info.Type == typeof(RemoteServerInfo))
                        info.Properties.Single(property => property.Name == nameof(Models.RemoteServerInfo.Password))
                            .ShouldSerialize = static (_, _) => false;
                })
            };
            options.MakeReadOnly();
            return (JsonTypeInfo<List<RemoteServerInfo>>)options.GetTypeInfo(typeof(List<RemoteServerInfo>));
        }
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<MacContextMenuService.LaunchServicesApp>))]
[JsonSerializable(typeof(MacImageAnalysisService.NativeAnalysisResult))]
internal sealed partial class NativeJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AppStoreUpdateService.LookupResponse))]
[JsonSerializable(typeof(VersionCheckResponse))]
internal sealed partial class WebAppJsonContext : JsonSerializerContext;

internal sealed record FileRecoveryRecord(string Source, string Destination, string Backup, bool Move);
internal sealed record NetworkSmokeReport(IReadOnlyList<NetworkServer> Servers, string? Error);
internal sealed record ConversionPaths([property: JsonPropertyName("paths")] string[] Paths);
