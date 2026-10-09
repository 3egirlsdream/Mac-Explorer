using System.Text;
using MacExplorer.Services;

namespace MacExplorer.Copilot;

public sealed class CopilotSettings(ISettingsService settings)
{
    private string ConsentSignature(string? key) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        Encoding.UTF8.GetBytes(Endpoint + "\n" + Model + "\n" + key)));
    public bool HasMetadataConsent(string? key) => settings.Get("copilot.metadata-consent") == ConsentSignature(key);
    public void AllowMetadataSharing(string? key) => settings.Set("copilot.metadata-consent", ConsentSignature(key));
    public void RevokeMetadataSharing() => settings.Set("copilot.metadata-consent", "");
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
