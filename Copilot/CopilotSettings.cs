using MacExplorer.Services;

namespace MacExplorer.Copilot;

public sealed class CopilotSettings(ISettingsService settings)
{
    public const string MetadataConsentKey = "copilot.metadata-consent";
    public bool HasMetadataConsent()
    {
        var consent = settings.Get(MetadataConsentKey);
        // Earlier versions stored a SHA-256 receiver signature after explicit approval.
        return consent == "allowed" || consent is { Length: 64 } && consent.All(Uri.IsHexDigit);
    }
    public void AllowMetadataSharing() => settings.Set(MetadataConsentKey, "allowed");
    public void RevokeMetadataSharing() => settings.Set(MetadataConsentKey, "");
    public const string EnabledKey = "copilot.enabled";
    public bool Enabled
    {
        get => settings.Get(EnabledKey, false);
        set => settings.Set(EnabledKey, value);
    }
    public const string EndpointKey = "copilot.endpoint";
    public const string ModelKey = "copilot.model";
    public string Endpoint
    {
        get => settings.Get(EndpointKey) ?? "https://api.openai.com/v1";
        set => settings.Set(EndpointKey, value.Trim());
    }
    public string Model
    {
        get => settings.Get(ModelKey) ?? "gpt-4.1-mini";
        set => settings.Set(ModelKey, value.Trim());
    }
}

/// <summary>Stores the API key in the local application database.</summary>
public sealed class CopilotCredentialStore
{
    private readonly Services.Impl.DatabaseCredentialStore _store = new("com.macexplorer.copilot");
    public string? Read() => _store.Read("api-key") ?? (RuntimePaths.TestRoot != null
        ? Environment.GetEnvironmentVariable("MACEXPLORER_COPILOT_TEST_KEY") : null);
    public void Save(string key) => _store.Save("api-key", key);
}
