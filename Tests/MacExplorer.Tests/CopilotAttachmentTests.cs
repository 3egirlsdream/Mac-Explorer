using System.Text.Json;
using MacExplorer.Copilot;
using Xunit;

namespace MacExplorer.Tests;

public sealed class CopilotAttachmentTests
{
    [Fact]
    public void DroppedFilesAndFoldersBecomeDistinctPathsWithoutMovingThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "fk-copilot-attachments-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "资料");
        var file = Path.Combine(root, "清单.txt");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(file, "test");
            var attachments = CopilotAttachments.FromPaths([folder + Path.DirectorySeparatorChar, file, file,
                Path.Combine(root, "missing")]);

            Assert.Equal(2, attachments.Length);
            Assert.Equal(new CopilotAttachment(folder, true), attachments[0]);
            Assert.Equal(new CopilotAttachment(file, false), attachments[1]);
            Assert.True(Directory.Exists(folder));
            Assert.True(File.Exists(file));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PromptCarriesExactPathsAndTranscriptDataCanBeRestored()
    {
        CopilotAttachment[] attachments =
        [
            new("/tmp/报告 文件.txt", false),
            new("/tmp/资料夹", true)
        ];
        var prompt = CopilotAttachments.BuildPrompt("整理这两个", attachments);
        Assert.StartsWith("整理这两个\n\n本轮附件", prompt);
        Assert.Contains("未移动", prompt);
        Assert.Contains("/tmp/报告 文件.txt", prompt);
        Assert.Contains("\"Type\":\"folder\"", prompt);
        Assert.Equal(attachments, CopilotAttachments.Parse(JsonSerializer.Serialize(attachments)));
    }
}
