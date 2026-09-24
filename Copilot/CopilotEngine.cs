using System.ComponentModel;
using System.Text.Json;
using System.Text.Encodings.Web;
using Avalonia.Threading;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;
using MacExplorer.ViewModels;

namespace MacExplorer.Copilot;

public sealed record CopilotReply(string Text, CapabilityPlan? ApprovalPlan, bool NeedsApproval);

/// <summary>A single window's MAF agent and session. No application credential is a tool.</summary>
public sealed class CopilotEngine(
    IAppCapabilityRegistry capabilities, CopilotSettings settings, CopilotKeychain keychain,
    CopilotStore store, CopilotSkillCatalog skillCatalog, Func<FileListViewModel?> activePane)
{
    private static readonly JsonSerializerOptions CatalogJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private ChatClientAgent? _agent;
    private AgentSession? _session;
    private ToolApprovalRequestContent? _approval;
    private readonly Queue<ToolApprovalRequestContent> _pendingApprovals = new();
    private readonly List<AIContent> _approvalResponses = [];
    private readonly Dictionary<string, CapabilityPlan> _plans = new(StringComparer.Ordinal);
    private readonly HashSet<string> _handledApprovalCalls = new(StringComparer.Ordinal);
    private string? _configSignature;
    private HttpClient? _modelHttpClient;
    private string _sessionId = store.LatestSessionId() ?? store.CreateSession();

    public string SessionId => _sessionId;
    public long CompletedOperationCount { get; private set; }
    public string? LastCompletedOperationMessage { get; private set; }
    public event Action<string>? Activity;

    public IReadOnlyList<CopilotTurn> History => store.GetTurns(_sessionId);

    public void NewSession()
    {
        SwitchToSession(store.CreateSession());
    }

    public void SwitchSession(string id)
    {
        if (!store.ListSessions().Any(session => session.Id == id))
            throw new ArgumentException("会话不存在。", nameof(id));
        if (id != _sessionId) SwitchToSession(id);
    }

    private void SwitchToSession(string id)
    {
        foreach (var plan in _plans.Values) capabilities.CancelPlan(plan.Id);
        _sessionId = id;
        _session = null;
        _approval = null;
        _pendingApprovals.Clear();
        _approvalResponses.Clear();
        _plans.Clear();
        _handledApprovalCalls.Clear();
        LastCompletedOperationMessage = null;
    }

    public void DeleteCurrentSession()
    {
        store.DeleteSession(_sessionId);
        SwitchToSession(store.LatestSessionId() ?? store.CreateSession());
    }

    public async Task<CopilotReply> SendAsync(string message, IReadOnlyList<CopilotAttachment>? attachments = null,
        Action<AgentResponseUpdate>? onUpdate = null,
        CancellationToken cancellationToken = default)
    {
        if (_approval != null) throw new InvalidOperationException("请先处理待确认的操作。");
        attachments ??= [];
        store.AddTurn(_sessionId, "user", message);
        if (attachments.Count > 0)
            store.AddTurn(_sessionId, "user-attachments", JsonSerializer.Serialize(attachments));
        await EnsureAgentAsync(cancellationToken);
        return await RunAndRecordAsync(
            _agent!.RunStreamingAsync(CopilotAttachments.BuildPrompt(message, attachments), _session,
                cancellationToken: cancellationToken),
            onUpdate, cancellationToken);
    }

    public async Task<CopilotReply> RespondToApprovalAsync(bool approved, Action<AgentResponseUpdate>? onUpdate = null,
        CancellationToken cancellationToken = default)
    {
        var request = _approval ?? throw new InvalidOperationException("没有待确认的操作。");
        _handledApprovalCalls.Add(request.ToolCall.CallId);
        if (!approved && TryGetApprovalPlan(request) is { } declined)
        {
            _plans.Remove(declined.Id);
            capabilities.CancelPlan(declined.Id);
        }
        Activity?.Invoke(approved ? "用户已批准操作" : "用户已拒绝操作");
        _approvalResponses.Add(request.CreateResponse(approved));
        _pendingApprovals.Dequeue();
        _approval = _pendingApprovals.TryPeek(out var next) ? next : null;
        if (_approval != null)
            return new("", TryGetApprovalPlan(_approval), true);

        var responses = _approvalResponses.ToArray();
        _approvalResponses.Clear();
        return await RunAndRecordAsync(_agent!.RunStreamingAsync(
            new ChatMessage(ChatRole.User, responses),
            _session, cancellationToken: cancellationToken), onUpdate, cancellationToken);
    }

    internal static async Task<AgentResponse> CollectStreamingResponseAsync(
        IAsyncEnumerable<AgentResponseUpdate> stream, Action<string>? onTextDelta,
        Action<AgentResponseUpdate>? onUpdate = null)
    {
        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in stream)
        {
            updates.Add(update);
            onUpdate?.Invoke(update);
            if ((update.Role == null || update.Role == ChatRole.Assistant)
                && !string.IsNullOrEmpty(update.Text))
                onTextDelta?.Invoke(update.Text);
        }
        // Keep the complete message contents: approval requests and tool results are not text deltas.
        return updates.ToAgentResponse();
    }

    private async Task<CopilotReply> RunAndRecordAsync(IAsyncEnumerable<AgentResponseUpdate> stream,
        Action<AgentResponseUpdate>? onUpdate, CancellationToken cancellationToken)
    {
        var trace = new CopilotRunTrace();
        var succeeded = false;
        try
        {
            var response = await CollectStreamingResponseAsync(stream, null, update =>
            {
                trace.Apply(update);
                onUpdate?.Invoke(update);
            });
            var reply = await FinishAsync(response, cancellationToken);
            succeeded = true;
            return reply;
        }
        finally
        {
            trace.Finish(!succeeded);
            foreach (var step in trace.Steps)
            {
                if (step.Kind == "assistant" && !string.IsNullOrWhiteSpace(step.Text))
                    store.AddTurn(_sessionId, "assistant-step", step.Text);
                else if (step.Kind == "tools" && step.Calls.Count > 0)
                    store.AddTurn(_sessionId, "tool-batch", JsonSerializer.Serialize(step));
                else if (step.Kind == "thinking")
                    store.AddTurn(_sessionId, "thinking", step.Text);
            }
        }
    }

    private async Task EnsureAgentAsync(CancellationToken cancellationToken)
    {
        var key = keychain.Read();
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("请在设置中配置 Copilot API Key。");
        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps
                && !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback))
            throw new InvalidOperationException("Copilot API 地址须使用 HTTPS；本机回环地址可使用 HTTP。");
        if (string.IsNullOrWhiteSpace(settings.Model))
            throw new InvalidOperationException("请配置 Copilot 模型名。");
        var signature = $"{settings.Endpoint}\n{settings.Model}\n{key}";
        if (_agent != null && signature == _configSignature && _session != null) return;

        var httpClient = new HttpClient(new ReasoningReplayHandler(
            new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(30),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            }, store, () => _sessionId)) { Timeout = TimeSpan.FromMinutes(10) };
        var chatClient = new OpenAI.Chat.ChatClient(settings.Model, new ApiKeyCredential(key),
            new OpenAIClientOptions
            {
                Endpoint = endpoint,
                Transport = new HttpClientPipelineTransport(httpClient)
            }).AsIChatClient();
        var skills = new AgentSkillsProviderBuilder()
            .UseFileSkill(skillCatalog.DirectoryPath)
            .UseFileScriptRunner((_, _, _, _, _) => throw new InvalidOperationException("Copilot 技能不能运行脚本。"))
            .UseFilter((skill, _) => skillCatalog.List().Any(item => item.Name == skill.Frontmatter.Name && item.Enabled))
            .UseOptions(options => options.DisableLoadSkillApproval = true)
            .DisableCaching().Build();
        var options = new ChatClientAgentOptions
        {
            Name = "copilot",
            ChatOptions = new ChatOptions
            {
                Instructions = "你是 Mac Explorer 内置 Copilot。只通过已登记能力操作文件；已知能力 ID 时直接查看其说明，否则搜索能力目录。搜索无结果不能证明能力不存在，应拆分关键词核对。修改操作先预览，再请求执行。" +
                    "绝不把文件正文、密钥或凭据自行读取或发送。当前窗格和选中项仅作为定位线索。" +
                    "file.content 每次只返回一页；需要后续正文时使用返回的 NextOffset 重新预览并请求批准。只有 HasMore=false 才能声称读完全文。" +
                    "使用候选文件集中的路径时，先读取对应产物，逐字复制 FullPath，不要根据文件名重写路径；单个路径失败时优先核对候选集。" +
                    "用中文简洁回答；每项失败要说清楚。",
                Tools =
                [
                    AIFunctionFactory.Create(SearchCapabilities),
                    AIFunctionFactory.Create(DescribeCapability),
                    AIFunctionFactory.Create(ReadArtifact),
                    AIFunctionFactory.Create(SaveClassificationProposal),
                    AIFunctionFactory.Create(CallReadOnlyAsync),
                    AIFunctionFactory.Create(PreviewOperationAsync),
                    new ApprovalRequiredAIFunction(AIFunctionFactory.Create(ExecuteApprovedPlanAsync))
                ]
            },
            AIContextProviders = [new WorkspaceContextProvider(activePane), new PaneContextProvider(activePane),
                new SelectionContextProvider(activePane),
                new SearchResultsContextProvider(activePane), new ArtifactIndexContextProvider(store, () => _sessionId),
                new ApprovedContentContextProvider(store, () => _sessionId),
                new PendingPlanContextProvider(() => _plans.Values.ToArray()), skills]
        };
        _agent = new ChatClientAgent(chatClient, options);
        _modelHttpClient?.Dispose();
        _modelHttpClient = httpClient;
        _configSignature = signature;
        var saved = store.LoadState(_sessionId);
        _session = saved == null
            ? await _agent.CreateSessionAsync(cancellationToken: cancellationToken)
            : await _agent.DeserializeSessionAsync(JsonDocument.Parse(saved).RootElement,
                cancellationToken: cancellationToken);
    }

    private async Task<CopilotReply> FinishAsync(AgentResponse response, CancellationToken cancellationToken)
    {
        var text = response.Text ?? string.Empty;
        _pendingApprovals.Clear();
        foreach (var request in response.Messages.SelectMany(x => x.Contents)
                     .OfType<ToolApprovalRequestContent>()
                     .Where(request => !_handledApprovalCalls.Contains(request.ToolCall.CallId)))
            _pendingApprovals.Enqueue(request);
        _approval = _pendingApprovals.TryPeek(out var first) ? first : null;
        var plan = TryGetApprovalPlan(_approval);
        if (_session != null)
        {
            var serialized = await _agent!.SerializeSessionAsync(_session, cancellationToken: cancellationToken);
            store.SaveState(_sessionId, serialized.GetRawText());
        }
        return new(text, plan, _approval != null);
    }

    private CapabilityPlan? TryGetApprovalPlan(ToolApprovalRequestContent? request)
    {
        if (request?.ToolCall is not FunctionCallContent call
            || call.Arguments == null || !call.Arguments.TryGetValue("planId", out var raw)) return null;
        return raw != null && _plans.TryGetValue(raw.ToString()!, out var plan) ? plan : null;
    }

    [Description("搜索 Mac Explorer 可用能力目录，返回稳定 ID、影响和确认要求。已知 ID 请直接调用 DescribeCapability。")]
    internal string SearchCapabilities([Description("功能关键词；多个词以空格分隔，匹配任一词；空字符串列出全部") ] string query)
    {
        query ??= string.Empty;
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var exact = capabilities.Catalog.FirstOrDefault(c => c.Id.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase));
        AppCapability[] matches = exact != null ? [exact] : capabilities.Catalog.Where(c => terms.Length == 0
            || terms.Any(term => c.Id.Contains(term, StringComparison.OrdinalIgnoreCase)
                || c.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || c.Description.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
        Activity?.Invoke($"搜索可用能力：{(string.IsNullOrWhiteSpace(query) ? "全部" : query)} · {matches.Length} 项");
        return JsonSerializer.Serialize(matches.Select(c => new { c.Id, c.Name, c.Description, c.Impact, c.Confirmation }), CatalogJsonOptions);
    }

    [Description("按已知稳定 ID 查看能力的参数格式、原逻辑归属、影响与确认规则，无需先搜索。")]
    internal string DescribeCapability([Description("稳定能力 ID") ] string id)
    {
        var capability = capabilities.Find(id);
        if (capability == null)
            return JsonSerializer.Serialize(new CapabilityResult(false, $"未知能力 ID：{id}"), CatalogJsonOptions);
        Activity?.Invoke($"查看能力说明：{capability.Name}");
        return JsonSerializer.Serialize(capability, CatalogJsonOptions);
    }

    [Description("按产物 ID 读取本会话中的候选文件集或分类方案。文件正文必须重新逐次审批，不能通过此工具读取。")]
    private string ReadArtifact([Description("候选文件集或分类方案产物 ID") ] string artifactId)
    {
        var artifact = store.GetArtifact(_sessionId, artifactId);
        if (artifact?.Kind is not ("candidate-file-set" or "classification-proposal"))
            throw new InvalidOperationException("只能读取本会话中的候选文件集或分类方案。");
        Activity?.Invoke($"读取会话产物：{artifact.Kind}");
        return artifact.Value;
    }

    [Description("把候选文件集整理成分类方案产物，只记录分类建议，不修改文件。paths 必须来自该候选集。")]
    internal string SaveClassificationProposal(
        [Description("此前 file.list、file.search 或 tag.files 返回的候选文件集 ID") ] string candidateArtifactId,
        [Description("JSON 数组，例如 [{\"name\":\"图片\",\"paths\":[\"/path/a.png\"]}]") ] string groupsJson)
    {
        var source = store.GetArtifact(_sessionId, candidateArtifactId);
        if (source?.Kind != "candidate-file-set")
            throw new ArgumentException("候选文件集不存在。");
        using var sourceDocument = JsonDocument.Parse(source.Value);
        var data = sourceDocument.RootElement.GetProperty("Data");
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in data.EnumerateArray())
        {
            var path = item.ValueKind == JsonValueKind.String ? item.GetString()
                : item.TryGetProperty("FullPath", out var property) ? property.GetString() : null;
            if (!string.IsNullOrWhiteSpace(path)) allowed.Add(path);
        }
        using var groupsDocument = JsonDocument.Parse(groupsJson);
        if (groupsDocument.RootElement.ValueKind != JsonValueKind.Array
            || groupsDocument.RootElement.GetArrayLength() is < 1 or > 100)
            throw new ArgumentException("分类方案需要 1 到 100 组。");
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<object>();
        foreach (var group in groupsDocument.RootElement.EnumerateArray())
        {
            var name = group.GetProperty("name").GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 100
                || name.IndexOfAny(['/', '\\', '\0']) >= 0)
                throw new ArgumentException("分类名称无效。");
            var paths = group.GetProperty("paths").EnumerateArray()
                .Select(path => path.GetString() ?? "").ToArray();
            if (paths.Length == 0 || paths.Any(path => !allowed.Contains(path) || !assigned.Add(path)))
                throw new ArgumentException("分类文件必须来自候选集且不能重复。");
            groups.Add(new { name, paths });
        }
        var id = Guid.NewGuid().ToString("N");
        store.SaveArtifact(id, _sessionId, "classification-proposal",
            JsonSerializer.Serialize(new { candidateArtifactId, groups, createdAt = DateTimeOffset.UtcNow }));
        Activity?.Invoke($"已保存分类方案：{groups.Count} 组、{assigned.Count} 个文件");
        return JsonSerializer.Serialize(new { artifactId = id, groupCount = groups.Count, fileCount = assigned.Count });
    }

    [Description("调用只读应用能力。写操作必须使用预览与批准计划工具。")]
    private async Task<string> CallReadOnlyAsync(
        [Description("稳定能力 ID") ] string id,
        [Description("JSON 参数对象") ] string argumentsJson)
    {
        var result = await capabilities.ExecuteReadAsync(id, argumentsJson, activePane());
        Activity?.Invoke($"调用只读能力：{capabilities.Find(id)?.Name ?? id}");
        if (id is not ("file.list" or "file.search" or "tag.files")) return JsonSerializer.Serialize(result);
        var artifactId = Guid.NewGuid().ToString("N");
        store.SaveArtifact(artifactId, _sessionId, "candidate-file-set", JsonSerializer.Serialize(result));
        return JsonSerializer.Serialize(new { artifactId, result });
    }

    [Description("预览文件修改的范围与冲突，生成需要用户确认的短时计划。")]
    internal async Task<string> PreviewOperationAsync(
        [Description("稳定能力 ID") ] string id,
        [Description("JSON 参数对象") ] string argumentsJson)
    {
        CapabilityPlan plan;
        try
        {
            plan = await capabilities.PreviewAsync(id, argumentsJson, activePane());
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException or JsonException)
        {
            return JsonSerializer.Serialize(new CapabilityResult(false, ex.Message), CatalogJsonOptions);
        }
        _plans[plan.Id] = plan;
        store.SaveArtifact(plan.Id, _sessionId, "operation-plan", JsonSerializer.Serialize(plan));
        if (id == "file.create-text")
            store.SaveArtifact(Guid.NewGuid().ToString("N"), _sessionId, "generation-draft", argumentsJson);
        Activity?.Invoke($"预览操作：{plan.Summary}");
        return JsonSerializer.Serialize(new { plan.Id, plan.Summary, plan.AffectedPaths, plan.Capability.Confirmation });
    }

    [Description("请求用户批准并执行已预览的计划。此工具需要人工批准，执行前再次核对文件状态。")]
    private async Task<string> ExecuteApprovedPlanAsync([Description("预览返回的计划 ID") ] string planId)
    {
        if (!_plans.TryGetValue(planId, out var plan))
            throw new InvalidOperationException("未知计划。请重新预览。");
        try
        {
            var result = await Dispatcher.UIThread.InvokeAsync(() =>
                capabilities.ExecuteApprovedAsync(planId, activePane(),
                    new Progress<string>(message => Activity?.Invoke(message))));
            CompletedOperationCount++;
            LastCompletedOperationMessage = result.Message;
            store.SaveArtifact(Guid.NewGuid().ToString("N"), _sessionId,
                plan.Capability.Id == "file.content" ? "content-excerpt" : "execution-receipt",
                JsonSerializer.Serialize(result));
            Activity?.Invoke(result.Message);
            return JsonSerializer.Serialize(result, plan.Capability.Id == "file.content" ? CatalogJsonOptions : null);
        }
        finally { _plans.Remove(planId); }
    }
}
