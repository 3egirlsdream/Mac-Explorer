using System.Text.Json;
using System.Text.Encodings.Web;

namespace MacExplorer.Copilot;

public sealed record CopilotAttachment(string Path, bool IsDirectory)
{
    public string Name => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path));
}

public static class CopilotAttachments
{
    private static readonly JsonSerializerOptions PromptJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static CopilotAttachment[] FromPaths(IEnumerable<string> paths)
    {
        var result = new List<CopilotAttachment>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in paths)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !System.IO.Path.IsPathFullyQualified(candidate)) continue;
            var path = System.IO.Path.TrimEndingDirectorySeparator(candidate);
            if (!seen.Add(path)) continue;
            if (Directory.Exists(path)) result.Add(new(path, true));
            else if (File.Exists(path)) result.Add(new(path, false));
        }
        return [.. result];
    }

    public static string BuildPrompt(string message, IReadOnlyList<CopilotAttachment> attachments)
        => attachments.Count == 0 ? message :
            $"{message}\n\n本轮附件仅提供本地路径，文件未移动，也未读取内容。请按用户要求通过已登记能力处理这些路径：\n" +
            JsonSerializer.Serialize(attachments.Select(item => new { item.Path, Type = item.IsDirectory ? "folder" : "file" }),
                PromptJsonOptions);

    public static CopilotAttachment[] Parse(string json)
    {
        try { return JsonSerializer.Deserialize<CopilotAttachment[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
