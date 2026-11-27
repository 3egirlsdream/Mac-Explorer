using System.Text.Json;
using System.Text.Json.Serialization;

namespace MacExplorer.PluginSdk;

public sealed class PluginManifestJsonConverter : JsonConverter<PluginManifest>
{
    public override PluginManifest? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => JsonSerializer.Deserialize(ref reader, PluginManifestPayloadJsonContext.Default.PluginManifestPayload)?.ToManifest();

    public override void Write(Utf8JsonWriter writer, PluginManifest value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, PluginManifestPayloadJsonContext.Default.PluginManifest);
}

// Setters keep defaults for omitted keys while preserving explicit nulls for validation.
// The SDK's existing init accessors remain available to compiled plugins.
internal sealed class PluginManifestPayload
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public int ApiVersion { get; set; }
    public string Entry { get; set; } = "";
    public string Icon { get; set; } = "convert";
    public string Description { get; set; } = "";
    public string Developer { get; set; } = "";
    public string Platform { get; set; } = "osx";
    public string Architecture { get; set; } = "arm64";
    public bool Paid { get; set; }
    public int TrialDays { get; set; }
    public bool HasUserInterface { get; set; }
    public PluginCommandPayload[]? Commands { get; set; } = [];

    internal PluginManifest ToManifest() => new()
    {
        Id = Id, Name = Name, Version = Version, ApiVersion = ApiVersion, Entry = Entry,
        Icon = Icon, Description = Description, Developer = Developer, Platform = Platform, Architecture = Architecture,
        Paid = Paid, TrialDays = TrialDays, HasUserInterface = HasUserInterface,
        Commands = Commands?.Select(command => command?.ToCommand()!).ToArray()!
    };
}

internal sealed class PluginCommandPayload
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Icon { get; set; } = "convert";
    public PluginMatchPayload? Match { get; set; } = new();

    internal PluginCommand ToCommand() => new() { Id = Id, Title = Title, Icon = Icon, Match = Match?.ToMatch()! };
}

internal sealed class PluginMatchPayload
{
    public string[] Extensions { get; set; } = [];
    public string[] FileNames { get; set; } = [];
    public bool TextFiles { get; set; }
    public int MinSelection { get; set; } = 1;
    public int MaxSelection { get; set; } = 1;

    internal PluginMatch ToMatch() => new()
    {
        Extensions = Extensions, FileNames = FileNames,
        TextFiles = TextFiles, MinSelection = MinSelection, MaxSelection = MaxSelection
    };
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PluginManifest))]
[JsonSerializable(typeof(PluginManifestPayload))]
internal sealed partial class PluginManifestPayloadJsonContext : JsonSerializerContext;
