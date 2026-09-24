using Avalonia.Headless.XUnit;
using MacExplorer.Copilot;
using MacExplorer.Services.Impl;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace MacExplorer.Tests;

public sealed class CopilotCoreTests
{
    [Fact]
    public async Task StreamingDeltasProduceOneCompleteAssistantReply()
    {
        var deltas = new List<string>();
        var response = await CopilotEngine.CollectStreamingResponseAsync(Updates(), deltas.Add);
        Assert.Equal(["**Hello", " world**"], deltas);
        Assert.Equal("**Hello world**", response.Text);

        static async IAsyncEnumerable<AgentResponseUpdate> Updates()
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, "**Hello") { MessageId = "answer" };
            await Task.Yield();
            yield return new AgentResponseUpdate(ChatRole.Assistant, " world**") { MessageId = "answer" };
        }
    }

    [Fact]
    public void ToolBatchKeepsModelSpeechBeforeCallsAndSummaryAfterResults()
    {
        var trace = new CopilotRunTrace();
        trace.Apply(new AgentResponseUpdate(ChatRole.Assistant, "我先查询文件。") { MessageId = "first" });
        trace.Apply(new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call-1", "CallReadOnly", new Dictionary<string, object?>
            {
                ["id"] = "file.info", ["argumentsJson"] = "{\"path\":\"/tmp/a.txt\"}"
            }),
            new FunctionCallContent("call-2", "CallReadOnly", new Dictionary<string, object?>
            {
                ["id"] = "file.info", ["argumentsJson"] = "{\"path\":\"/tmp/b.txt\"}"
            })
        }) { MessageId = "first" });
        var batch = Assert.Single(trace.Steps.Where(step => step.Kind == "tools"));
        Assert.Equal(2, batch.Calls.Count);
        Assert.Contains("/tmp/a.txt", batch.Calls[0].Detail);
        Assert.False(batch.Finished);

        trace.Apply(new AgentResponseUpdate(ChatRole.Tool, new List<AIContent>
        {
            new FunctionResultContent("call-1", "{\"Success\":false,\"Message\":\"文件不存在\"}"),
            new FunctionResultContent("call-2", "{\"Success\":true,\"Message\":\"已读取文件信息\"}")
        }));
        Assert.True(batch.Finished);
        Assert.True(batch.Calls[0].Failed);
        trace.Apply(new AgentResponseUpdate(ChatRole.Assistant, "第一个路径无效，我已继续核对。")
            { MessageId = "second" });
        trace.Finish();
        Assert.Equal(["thinking", "assistant", "tools", "thinking", "assistant"],
            trace.Steps.Select(step => step.Kind));
        Assert.Equal("我先查询文件。", trace.Steps[1].Text);
        Assert.Equal("第一个路径无效，我已继续核对。", trace.Steps[^1].Text);
    }

    [Fact]
    public void ReasoningDeltasRemainInTheirOwnStepAfterToolCalls()
    {
        var trace = new CopilotRunTrace();
        trace.Apply(new AgentResponseUpdate(ChatRole.Assistant,
            [new TextReasoningContent("先搜索")]) { MessageId = "first" });
        trace.Apply(new AgentResponseUpdate(ChatRole.Assistant,
            [new TextReasoningContent("文件。")]) { MessageId = "first" });
        trace.Apply(new AgentResponseUpdate(ChatRole.Assistant, "我来查询。") { MessageId = "first" });
        trace.Apply(new AgentResponseUpdate(ChatRole.Assistant,
            [new FunctionCallContent("call-1", "SearchCapabilities",
                new Dictionary<string, object?> { ["query"] = "文件" })]) { MessageId = "first" });
        trace.Apply(new AgentResponseUpdate(ChatRole.Tool,
            [new FunctionResultContent("call-1", "[]")]));
        trace.Apply(new AgentResponseUpdate(ChatRole.Assistant,
            [new TextReasoningContent("找到能力。")]) { MessageId = "second" });
        trace.Finish();

        Assert.Equal(["thinking", "assistant", "tools", "thinking"],
            trace.Steps.Select(step => step.Kind));
        Assert.Equal("先搜索文件。", trace.Steps[0].Text);
        Assert.Equal("我来查询。", trace.Steps[1].Text);
        Assert.Equal("找到能力。", trace.Steps[3].Text);
    }

    [Fact]
    public void InterruptedToolBatchShowsFailureInsteadOfRunningForever()
    {
        var trace = new CopilotRunTrace();
        trace.Apply(new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call-1", "CallReadOnly", new Dictionary<string, object?>
            {
                ["id"] = "file.info"
            })
        }) { MessageId = "first" });
        trace.Finish(failed: true);
        var batch = Assert.Single(trace.Steps.Where(step => step.Kind == "tools"));
        Assert.True(batch.Finished);
        Assert.True(Assert.Single(batch.Calls).Failed);
    }

    [Fact]
    public async Task EveryRegisteredCapabilityHasUniqueIdAndMutationCannotUseReadEntry()
    {
        var registry = new AppCapabilityRegistry(null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);
        Assert.Equal(registry.Catalog.Count, registry.Catalog.Select(item => item.Id).Distinct().Count());
        Assert.All(registry.Catalog, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Owner));
            Assert.False(string.IsNullOrWhiteSpace(item.Arguments));
            Assert.False(string.IsNullOrWhiteSpace(item.Confirmation));
            using var _ = System.Text.Json.JsonDocument.Parse(item.Arguments);
        });
        Assert.All(CommandOmniboxProvider.RegisteredCapabilityIds,
            id => Assert.NotNull(registry.Find(id)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.ExecuteReadAsync("file.trash", "{}", null));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.ExecuteReadAsync("file.content", "{}", null));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.ExecuteUiAsync("file.trash", "{}", null!));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.PreviewAsync("file.rename", "{}", null));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.ExecuteApprovedAsync("invented-plan", null));
    }

    [Fact]
    public void CapabilitySearchFindsKnownIdsInMultiWordQueries()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fk-copilot-search-" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new AppCapabilityRegistry(null!, null!, null!, null!, null!,
                null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);
            var engine = new CopilotEngine(registry, null!, null!,
                new CopilotStore(Path.Combine(directory, "copilot.db")), null!, () => null);

            using var combined = System.Text.Json.JsonDocument.Parse(engine.SearchCapabilities("file.info file.content"));
            Assert.Contains(combined.RootElement.EnumerateArray(), item => item.GetProperty("Id").GetString() == "file.info");
            Assert.Contains(combined.RootElement.EnumerateArray(), item => item.GetProperty("Id").GetString() == "file.content");

            using var chinese = System.Text.Json.JsonDocument.Parse(engine.SearchCapabilities("文件信息 元数据"));
            Assert.Contains(chinese.RootElement.EnumerateArray(), item => item.GetProperty("Id").GetString() == "file.info");

            using var exact = System.Text.Json.JsonDocument.Parse(engine.SearchCapabilities("file.info"));
            Assert.Equal("file.info", Assert.Single(exact.RootElement.EnumerateArray()).GetProperty("Id").GetString());

            using var described = System.Text.Json.JsonDocument.Parse(engine.DescribeCapability("file.info"));
            Assert.Equal("file.info", described.RootElement.GetProperty("Id").GetString());
            using var missing = System.Text.Json.JsonDocument.Parse(engine.DescribeCapability("file.unknown"));
            Assert.Equal("未知能力 ID：file.unknown", missing.RootElement.GetProperty("Message").GetString());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [AvaloniaFact]
    public async Task PreviewReturnsSpecificFailureInsteadOfGenericToolError()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fk-copilot-preview-" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new AppCapabilityRegistry(null!, null!, null!, null!, null!,
                null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);
            var engine = new CopilotEngine(registry, null!, null!,
                new CopilotStore(Path.Combine(directory, "copilot.db")), null!, () => null);

            using var response = System.Text.Json.JsonDocument.Parse(
                await engine.PreviewOperationAsync("file.create-text", "{}"));
            Assert.False(response.RootElement.GetProperty("Success").GetBoolean());
            Assert.Equal("请先激活文件窗格。", response.RootElement.GetProperty("Message").GetString());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void InstalledPluginCommandsAppearInCapabilityCatalog()
    {
        using var env = new PluginTestEnvironment();
        var registry = new AppCapabilityRegistry(null!, null!, null!, null!, env.Manager,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);
        var plugin = Assert.Single(env.Manager.Plugins);
        var commands = registry.Catalog.Where(item => item.Id.StartsWith("plugin.command:", StringComparison.Ordinal)).ToArray();
        Assert.Equal(plugin.Manifest.Commands.Length, commands.Length);
        Assert.All(commands, item => Assert.Equal(CapabilityImpact.Change, item.Impact));
    }

    [Fact]
    public async Task ContentPagesCoverLongLinesWithoutLosingCharacters()
    {
        var path = Path.Combine(Path.GetTempPath(), "fk-copilot-content-" + Guid.NewGuid().ToString("N") + ".html");
        try
        {
            var content = new string('a', CopilotContentExtractor.MaxPageUtf8Bytes - 1)
                + "😀" + new string('中', 12_000);
            await File.WriteAllTextAsync(path, content);
            var extractor = new CopilotContentExtractor(null!, null!);
            var first = await extractor.ExtractPageAsync(path);
            Assert.True(first.HasMore);
            Assert.Equal(CopilotContentExtractor.MaxPageUtf8Bytes - 1, first.NextOffset);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(first.Text) <= CopilotContentExtractor.MaxPageUtf8Bytes);

            var second = await extractor.ExtractPageAsync(path, first.NextOffset);
            Assert.False(second.HasMore);
            Assert.Equal(content, first.Text + second.Text);
            Assert.Equal(content.Length, second.NextOffset);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                extractor.ExtractPageAsync(path, content.Length + 1));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ClassificationProposalOnlyAcceptsPathsFromTheSameSessionCandidateSet()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fk-copilot-classify-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CopilotStore(Path.Combine(directory, "copilot.db"));
            var engine = new CopilotEngine(null!, null!, null!, store, null!, () => null);
            store.SaveArtifact("candidates", engine.SessionId, "candidate-file-set",
                "{\"Success\":true,\"Message\":\"ok\",\"Data\":[{\"FullPath\":\"/tmp/a.txt\"}]}");

            Assert.Throws<ArgumentException>(() => engine.SaveClassificationProposal(
                "candidates", "[{\"name\":\"文件\",\"paths\":[\"/tmp/not-listed.txt\"]}]"));
            var result = engine.SaveClassificationProposal(
                "candidates", "[{\"name\":\"文件\",\"paths\":[\"/tmp/a.txt\"]}]");
            var artifactId = System.Text.Json.JsonDocument.Parse(result).RootElement
                .GetProperty("artifactId").GetString()!;
            Assert.Equal("classification-proposal", store.GetArtifact(engine.SessionId, artifactId)?.Kind);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void SessionDeletionAlsoRemovesTranscriptAndArtifacts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fk-copilot-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "copilot.db");
            var store = new CopilotStore(path);
            var id = store.CreateSession();
            store.AddTurn(id, "user", "test message");
            store.SaveState(id, "{\"test\":true}");
            store.SaveArtifact("artifact-1", id, "plan", "{} ");
            var restored = new CopilotStore(path);
            Assert.Equal(id, restored.LatestSessionId());
            Assert.Equal("{\"test\":true}", restored.LoadState(id));
            Assert.Single(restored.GetTurns(id));
            Assert.Equal("plan", restored.GetArtifact(id, "artifact-1")?.Kind);
            Assert.Single(restored.ListArtifacts(id));
            restored.DeleteSession(id);
            Assert.Null(restored.LoadState(id));
            Assert.Empty(restored.GetTurns(id));
            Assert.Null(restored.GetArtifact(id, "artifact-1"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void SessionListSwitchAndDeleteRestoreTheRemainingConversation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fk-copilot-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CopilotStore(Path.Combine(directory, "copilot.db"));
            var first = store.CreateSession();
            store.AddTurn(first, "user", "整理  下载目录\n里的图片");
            store.AddTurn(first, "assistant-step", "已找到图片");
            store.SaveArtifact("first-artifact", first, "plan", "{}");
            var second = store.CreateSession();
            store.AddTurn(second, "user", "查询文件");

            var sessions = store.ListSessions();
            Assert.Equal([second, first], sessions.Select(session => session.Id));
            Assert.Equal("整理 下载目录 里的图片", sessions[1].Title);

            var engine = new CopilotEngine(null!, null!, null!, store, null!, () => null);
            engine.SwitchSession(first);
            Assert.Equal(first, engine.SessionId);
            Assert.Equal(2, engine.History.Count);
            Assert.Throws<ArgumentException>(() => engine.SwitchSession("missing"));

            engine.DeleteCurrentSession();
            Assert.Equal(second, engine.SessionId);
            Assert.Equal("查询文件", Assert.Single(engine.History).Text);
            Assert.Null(store.GetArtifact(first, "first-artifact"));
            Assert.Single(store.ListSessions());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
