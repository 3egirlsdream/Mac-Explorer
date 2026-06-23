using System.Text.Json.Serialization;

namespace MacExplorer.Models;

public class VersionCheckResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("data")]
    public VersionInfo? Data { get; set; }
}

public class VersionInfo
{
    [JsonPropertyName("CLIENT")]
    public string Client { get; set; } = "";

    [JsonPropertyName("DATETIME")]
    public string DateTime { get; set; } = "";

    [JsonPropertyName("ID")]
    public string Id { get; set; } = "";

    [JsonPropertyName("MEMO")]
    public string Memo { get; set; } = "";

    [JsonPropertyName("PATH")]
    public string Path { get; set; } = "";

    [JsonPropertyName("VERSION")]
    public string Version { get; set; } = "";
}
