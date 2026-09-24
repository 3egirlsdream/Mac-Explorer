using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MacExplorer.Copilot;
using MacExplorer.ViewModels;
using MacExplorer.Services;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class CopilotReviewRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fk-copilot-review-" + Guid.NewGuid().ToString("N"));
    private CopilotStore CreateStore() => new(Path.Combine(_root, "copilot.db"));

    [Fact]
    public void TwoWindowsCannotResumeOrDeleteAnOccupiedSessionAndDeletedStateCannotReturn()
    {
        var store = CreateStore();
        var session = store.CreateSession();
        store.AddTurn(session, "user", "first window");
        using var first = new CopilotEngine(null!, null!, null!, store, null!, () => null);
        using var second = new CopilotEngine(null!, null!, null!, new CopilotStore(Path.Combine(_root, "copilot.db")), null!, () => null);
        Assert.Equal(session, first.SessionId);
        Assert.NotEqual(session, second.SessionId);
        Assert.Throws<InvalidOperationException>(() => second.SwitchSession(session));
        Assert.Throws<InvalidOperationException>(() => store.DeleteSession(session));
        first.DeleteCurrentSession();
        Assert.Null(store.LoadState(session));
        Assert.Throws<InvalidOperationException>(() => store.SaveState(session, "late response"));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => store.AddTurn(session, "assistant", "late response"));
        Assert.DoesNotContain(store.ListSessions(), item => item.Id == session);
    }

    [Fact]
    public void ChangedRecipientAndLegacyHistoryStartFreshSessionsWithoutRestoringOldState()
    {
        var store = CreateStore();
        var settings = new TestSettings();
        var configuration = new CopilotSettings(settings);
        var old = store.CreateSession();
        store.SetSessionBoundary(old, "https://old.example/v1", "old-model");
        store.SaveState(old, "old private body and reasoning");
        store.AddTurn(old, "user", "old private body");
        configuration.Endpoint = "https://new.example/v1";
        configuration.Model = "new-model";
        using (var engine = new CopilotEngine(null!, configuration, null!, store, null!, () => null))
        {
            Assert.Equal(old, engine.SessionId);
            Assert.NotNull(engine.PrepareForSend());
            Assert.NotEqual(old, engine.SessionId);
            Assert.Null(store.LoadState(engine.SessionId));
            Assert.Empty(store.GetTurns(engine.SessionId));
            Assert.Equal("old private body and reasoning", store.LoadState(old));
        }
        var legacy = store.CreateSession();
        store.AddTurn(legacy, "user", "legacy private body");
        using var resumed = new CopilotEngine(null!, configuration, null!, store, null!, () => null);
        Assert.Equal(legacy, resumed.SessionId);
        Assert.NotNull(resumed.PrepareForSend());
        Assert.NotEqual(legacy, resumed.SessionId);
        Assert.Empty(store.GetTurns(resumed.SessionId));
    }

    [AvaloniaFact]
    public async Task ChangedEndpointRequestBodyContainsOnlyTheFreshConversation()
    {
        var store = CreateStore();
        var old = store.CreateSession();
        store.SetSessionBoundary(old, "https://old.example/v1", "old-model");
        store.SaveState(old, "old private body in agent state");
        store.AddTurn(old, "user", "old private body in transcript");
        store.SaveReasoning(old, "old-reasoning", "", "[]", "old private reasoning");
        const string reply = """
                data: {"id":"fresh","object":"chat.completion.chunk","created":1,"model":"new-model","choices":[{"index":0,"delta":{"role":"assistant","content":"fresh reply"},"finish_reason":null}]}

                data: {"id":"fresh","object":"chat.completion.chunk","created":1,"model":"new-model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                data: [DONE]

                """;
        var server = new CaptureModelHandler(reply);
        var settings = new CopilotSettings(new TestSettings())
        {
            Endpoint = "http://127.0.0.1:18549/v1",
            Model = "new-model"
        };
        var keychain = new CopilotKeychain();
        keychain.Save("local-test-key");
        using var engine = new CopilotEngine(new RecordingRegistry(), settings, keychain, store,
            new CopilotSkillCatalog(), () => null, () => server);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var replyResult = await engine.SendAsync("fresh prompt", cancellationToken: timeout.Token);
        var request = Assert.Single(server.Requests);
        Assert.Contains("fresh prompt", request);
        Assert.DoesNotContain("old private body", request);
        Assert.DoesNotContain("old private reasoning", request);
        Assert.Contains("fresh reply", replyResult.Text);
        Assert.NotEqual(old, engine.SessionId);
    }

    private sealed class CaptureModelHandler(string reply) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(reply, Encoding.UTF8, "text/event-stream")
            };
        }
    }

    [Fact]
    public async Task PendingApprovalAfterRestartOrSwitchRequiresFreshPreview()
    {
        var store = CreateStore();
        var settings = new CopilotSettings(new TestSettings());
        var old = store.CreateSession();
        store.SetSessionBoundary(old, settings.Endpoint, settings.Model, pending: true);
        store.SaveState(old, "pending agent state");
        using var engine = new CopilotEngine(null!, settings, null!, store, null!, () => null);
        Assert.Equal(old, engine.SessionId);
        Assert.NotNull(engine.PrepareForSend());
        Assert.NotEqual(old, engine.SessionId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ExecuteApprovedPlanAsync("old-plan"));
        engine.SwitchSession(old);
        Assert.NotNull(engine.PrepareForSend());
        Assert.NotEqual(old, engine.SessionId);
    }

    private sealed class TestSettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = [];
        public event Action<string>? SettingChanged;
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public T Get<T>(string key, T defaultValue) => defaultValue;
        public void Set(string key, string value) { _values[key] = value; SettingChanged?.Invoke(key); }
        public void Set<T>(string key, T value) => Set(key, value?.ToString() ?? "");
        public Dictionary<string, string> GetAll() => new(_values);
    }

    [AvaloniaFact]
    public async Task ReadAndPreviewToolsMarshalPaneAccessAndCapabilitiesToUiThread()
    {
        var registry = new RecordingRegistry();
        var paneReads = 0;
        var engine = new CopilotEngine(registry, null!, null!, CreateStore(), null!, () =>
        {
            Dispatcher.UIThread.VerifyAccess();
            paneReads++;
            return null;
        });
        // Agent tool callbacks need not originate on Avalonia's synchronization context.
        await Task.Run(async () =>
        {
            await engine.CallReadOnlyAsync("file.info", "{}");
            await engine.PreviewOperationAsync("file.rating", "{}");
        });
        Assert.Equal(2, paneReads);
        Assert.Equal(1, registry.ReadCalls);
        Assert.Equal(1, registry.PreviewCalls);
    }

    [AvaloniaFact]
    public async Task FailedAndCancelledResultsAreNotCountedAsCompletedOperations()
    {
        var store = CreateStore();
        var registry = new RecordingRegistry();
        var engine = new CopilotEngine(registry, null!, null!, store, null!, () => null);
        async Task Execute(bool success, string message)
        {
            registry.Result = new CapabilityResult(success, message);
            using var preview = JsonDocument.Parse(await engine.PreviewOperationAsync("file.rating", "{}"));
            using var result = JsonDocument.Parse(await engine.ExecuteApprovedPlanAsync(
                preview.RootElement.GetProperty("Id").GetString()!));
            Assert.Equal(success, result.RootElement.GetProperty("Success").GetBoolean());
        }
        await Execute(false, "用户取消了插件配置。");
        Assert.Equal(0L, engine.CompletedOperationCount);
        Assert.Null(engine.LastCompletedOperationMessage);
        await Execute(true, "已完成");
        await Execute(false, "部分项目失败");
        Assert.Equal(1L, engine.CompletedOperationCount);
        Assert.Equal("已完成", engine.LastCompletedOperationMessage);
        Assert.Equal(3, store.ListArtifacts(engine.SessionId).Count(item => item.Kind == "execution-receipt"));
    }

    [Fact]
    public void CandidatePagesRetainTheArrayContractAndExposeAllItems()
    {
        var items = Enumerable.Range(0, 401).Select(index => $"/tmp/{index:D4}.txt").ToArray();
        var collected = new List<string>();
        var offset = 0;
        do
        {
            using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { offset }));
            var result = CapabilityPagination.Create(items, arguments.RootElement, path => path);
            var page = Assert.IsType<CapabilityPage>(result.Page);
            var data = JsonSerializer.SerializeToElement(result).GetProperty("Data");
            Assert.Equal(JsonValueKind.Array, data.ValueKind);
            Assert.Equal(items.Length, page.TotalCount);
            Assert.Equal(offset, page.Offset);
            Assert.Equal(data.GetArrayLength(), page.ReturnedCount);
            Assert.InRange(page.ReturnedCount, 1, 200);
            collected.AddRange(data.EnumerateArray().Select(item => item.GetString()!));
            if (!page.HasMore) break;
            Assert.True(page.NextOffset!.Value > offset);
            offset = page.NextOffset!.Value;
        } while (true);
        Assert.Equal(items, collected);
        using var end = JsonDocument.Parse("{\"offset\":401}");
        var empty = CapabilityPagination.Create(items, end.RootElement, path => path);
        Assert.False(empty.Page!.HasMore);
        Assert.Equal(0, empty.Page.ReturnedCount);
    }

    [Theory]
    [InlineData("{\"offset\":-1}")]
    [InlineData("{\"offset\":1.5}")]
    [InlineData("{\"offset\":\"bad\"}")]
    [InlineData("{\"offset\":2}")]
    public void InvalidCandidateOffsetsDoNotSilentlyRestartTheList(string json)
    {
        using var arguments = JsonDocument.Parse(json);
        Assert.ThrowsAny<ArgumentException>(() =>
            CapabilityPagination.Create(new[] { "one" }, arguments.RootElement, path => path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponseReplayStaysWithTheSessionThatStartedTheRequest(bool streaming)
    {
        var store = CreateStore();
        var original = store.CreateSession();
        var other = store.CreateSession();
        var active = original;
        var body = streaming ? Event("bound", "first") + "data: [DONE]\n\n"
            : "{\"id\":\"bound\",\"choices\":[{\"message\":{\"content\":\"\",\"reasoning_content\":\"first\"}}]}";
        using var http = new HttpClient(new ReasoningReplayHandler(new ReplyHandler(() =>
        {
            active = other;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
            };
        }), store, () => active));
        using var request = Request();
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await response.Content.ReadAsStringAsync();
        Assert.Equal("first", Assert.Single(store.LoadReasoning(original)).ReasoningContent);
        Assert.Empty(store.LoadReasoning(other));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ZeroLengthReadsDoNotSaveAnIncompleteReasoningResponse(int overload)
    {
        var store = CreateStore();
        var session = store.CreateSession();
        var first = Event("zero-read", "first");
        var body = first + Event("zero-read", "second") + "data: [DONE]\n\n";
        using var http = new HttpClient(new ReasoningReplayHandler(new ReplyHandler(() =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
            }), store, () => session));
        using var request = Request();
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[Encoding.UTF8.GetByteCount(first)];
        await stream.ReadExactlyAsync(buffer.AsMemory());
        var count = overload switch
        {
            0 => stream.Read(Array.Empty<byte>(), 0, 0),
            1 => await stream.ReadAsync(Array.Empty<byte>(), 0, 0, CancellationToken.None),
            _ => await stream.ReadAsync(Memory<byte>.Empty)
        };
        Assert.Equal(0, count);
        Assert.Empty(store.LoadReasoning(session));
        await stream.CopyToAsync(Stream.Null);
        Assert.Equal("firstsecond", Assert.Single(store.LoadReasoning(session)).ReasoningContent);
    }

    private static string Event(string id, string text) => "data: " + JsonSerializer.Serialize(new
    {
        id, choices = new[] { new { delta = new { reasoning_content = text } } }
    }) + "\n\n";

    private static HttpRequestMessage Request() => new(HttpMethod.Post, "https://example.invalid/v1/chat/completions")
    {
        Content = new StringContent("{\"messages\":[]}", Encoding.UTF8, "application/json")
    };

    private sealed class ReplyHandler(Func<HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(reply());
    }

    private sealed class RecordingRegistry : IAppCapabilityRegistry
    {
        private static readonly AppCapability Capability = new("file.rating", "评分", "设置评分", "{}",
            "test", CapabilityImpact.Change, "确认");
        public CapabilityResult Result { get; set; } = new(true, "ok");
        public int ReadCalls { get; private set; }
        public int PreviewCalls { get; private set; }
        public IReadOnlyList<AppCapability> Catalog => [Capability];
        public AppCapability? Find(string id) => Capability;
        public Task<CapabilityResult> ExecuteReadAsync(string id, string argumentsJson, FileListViewModel? pane)
        {
            Dispatcher.UIThread.VerifyAccess();
            ReadCalls++;
            return Task.FromResult(Result);
        }
        public Task<CapabilityPlan> PreviewAsync(string id, string argumentsJson, FileListViewModel? pane)
        {
            Dispatcher.UIThread.VerifyAccess();
            PreviewCalls++;
            return Task.FromResult(new CapabilityPlan(Guid.NewGuid().ToString("N"), Capability,
                argumentsJson, "preview", [], DateTimeOffset.UtcNow));
        }
        public Task<CapabilityResult> ExecuteApprovedAsync(string planId, FileListViewModel? pane,
            IProgress<string>? progress = null)
        {
            Dispatcher.UIThread.VerifyAccess();
            return Task.FromResult(Result);
        }
        public Task<CapabilityResult> ExecuteUiAsync(string id, string argumentsJson, FileListViewModel pane)
            => throw new NotSupportedException();
        public void CancelPlan(string planId) { }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
