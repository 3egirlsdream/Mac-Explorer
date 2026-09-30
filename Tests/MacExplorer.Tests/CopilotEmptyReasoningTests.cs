using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using MacExplorer.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using Xunit;

namespace MacExplorer.Tests;

public sealed class CopilotEmptyReasoningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "copilot-empty-reasoning-" + Guid.NewGuid().ToString("N"));
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TwoConsecutiveApprovalsReplayEmptyReasoningAndComplete(bool sendEmptyField)
    {
        var store = new CopilotStore(Path.Combine(_root, "copilot.db"));
        var id = store.CreateSession();
        var server = new TwoApprovalServer(sendEmptyField);
        using var http = new HttpClient(new ReasoningReplayHandler(server, store, () => id));
        using var chat = new OpenAI.Chat.ChatClient("thinking-test", new ApiKeyCredential("test-key"),
            new OpenAIClientOptions { Endpoint = new Uri("https://example.test/v1"),
                Transport = new HttpClientPipelineTransport(http) }).AsIChatClient();
        var executed = new List<string>();
        var agent = new ChatClientAgent(chat, new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions
            {
                Tools = [new ApprovalRequiredAIFunction(AIFunctionFactory.Create((string planId) =>
                {
                    executed.Add(planId);
                    return "已读取正文 " + planId;
                }, "ExecuteApprovedPlan"))]
            }
        });
        var session = await agent.CreateSessionAsync(cancellationToken: Token);
        var response = await CopilotEngine.CollectStreamingResponseAsync(agent.RunStreamingAsync("读取两个 PDF", session, cancellationToken: Token), null);
        for (var i = 0; i < 2; i++)
        {
            var approval = Assert.Single(response.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>());
            response = await CopilotEngine.CollectStreamingResponseAsync(agent.RunStreamingAsync(
                new ChatMessage(ChatRole.User, [approval.CreateResponse(true)]), session, cancellationToken: Token), null);
        }
        Assert.Equal(["first", "second"], executed);
        Assert.Contains("两个文件已确认", response.Text);
        Assert.Equal(3, server.Requests.Count);
        using var last = JsonDocument.Parse(server.Requests[^1]);
        var calls = last.RootElement.GetProperty("messages").EnumerateArray()
            .Where(m => m.TryGetProperty("tool_calls", out _)).ToArray();
        Assert.Equal("First reasoning.", calls[0].GetProperty("reasoning_content").GetString());
        Assert.Equal("", calls[^1].GetProperty("reasoning_content").GetString());
        Assert.Contains(store.LoadReasoning(id), r => r.ToolCallIdsJson.Contains("call-second") && r.ReasoningContent == "");
    }

    [Fact]
    public async Task LegacyDeepSeekMessagesWithNoStoredReasoningReceiveEmptyField()
    {
        var store = new CopilotStore(Path.Combine(_root, "copilot.db"));
        var id = store.CreateSession();
        var server = new TwoApprovalServer(true);
        using var http = new HttpClient(new ReasoningReplayHandler(server, store, () => id));
        using var response = await http.PostAsync("https://api.deepseek.com/chat/completions", new StringContent("""
            {"messages":[{"role":"assistant","content":"第一个已确认"},
            {"role":"assistant","content":null,"tool_calls":[{"id":"legacy-call","type":"function","function":{"name":"test","arguments":"{}"}}]},
            {"role":"tool","tool_call_id":"legacy-call","content":"ok"}]}
            """, Encoding.UTF8, "application/json"), Token);
        using var sent = JsonDocument.Parse(Assert.Single(server.Requests));
        Assert.Equal("", sent.RootElement.GetProperty("messages")[0].GetProperty("reasoning_content").GetString());
        Assert.Equal("", sent.RootElement.GetProperty("messages")[1].GetProperty("reasoning_content").GetString());
    }

    private sealed class TwoApprovalServer(bool sendEmptyField) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            using var sent = JsonDocument.Parse(Requests[^1]);
            foreach (var message in sent.RootElement.GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("role").GetString() == "assistant"))
                if (!message.TryGetProperty("reasoning_content", out var reasoning) || reasoning.ValueKind != JsonValueKind.String)
                    return new(HttpStatusCode.BadRequest) { Content = new StringContent("reasoning_content missing") };
            var number = Requests.Count;
            var role = new Dictionary<string, object?> { ["role"] = "assistant" };
            if (number == 1) role["reasoning_content"] = "First reasoning.";
            else if (sendEmptyField) role["reasoning_content"] = "";
            var events = Event(role, null);
            if (number < 3)
            {
                var plan = number == 1 ? "first" : "second";
                events += Event(new { tool_calls = new[] { new { index = 0, id = "call-" + plan,
                    type = "function", function = new { name = "ExecuteApprovedPlan", arguments = JsonSerializer.Serialize(new { planId = plan }) } } } }, null);
                events += Event(new { }, "tool_calls");
            }
            else
            {
                events += Event(new { content = "两个文件已确认。" }, null);
                events += Event(new { }, "stop");
            }
            events += "data: [DONE]\n\n";
            return new(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") };
            string Event(object delta, string? finish) => "data: " + JsonSerializer.Serialize(new
            {
                id = "response-" + number, @object = "chat.completion.chunk", created = number, model = "thinking-test",
                choices = new[] { new { index = 0, delta, finish_reason = finish } }
            }) + "\n\n";
        }
    }
    public void Dispose() => Directory.Delete(_root, true);
}
