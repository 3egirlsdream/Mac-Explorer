using System.Text.Json;
using System.Text.Json.Serialization;

namespace MacExplorer.PluginSdk;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(PluginManifestJsonConverter)])]
[JsonSerializable(typeof(Dictionary<string, PluginTrial>))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(PluginAccessRequest))]
[JsonSerializable(typeof(PluginAccessResult))]
[JsonSerializable(typeof(PluginInitialization))]
[JsonSerializable(typeof(PluginInteractionRequest))]
[JsonSerializable(typeof(PluginInteractionResult))]
[JsonSerializable(typeof(PluginInvocation))]
[JsonSerializable(typeof(PluginManifest))]
[JsonSerializable(typeof(PluginPreparation))]
[JsonSerializable(typeof(PluginProgress))]
[JsonSerializable(typeof(PluginResult))]
[JsonSerializable(typeof(PluginRpcMessage))]
public sealed partial class PluginJsonContext : JsonSerializerContext;

public sealed record PluginInitialization(int ApiVersion, int ProcessId);
