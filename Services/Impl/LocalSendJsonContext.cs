using System.Text.Json;
using System.Text.Json.Serialization;

namespace MacExplorer.Services.Impl;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(LocalSendService.PeerInfo))]
[JsonSerializable(typeof(LocalSendService.PrepareRequest))]
[JsonSerializable(typeof(LocalSendService.PrepareResponse))]
[JsonSerializable(typeof(List<LocalSendService.VerifiedPeerHistory>))]
internal sealed partial class LocalSendJsonContext : JsonSerializerContext;
