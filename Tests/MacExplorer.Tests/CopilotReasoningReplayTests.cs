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

public sealed class CopilotReasoningReplayTests
{
    [Fact]
    public async Task StreamingAgentSeparatesToolIntroductionBatchAndFinalReply()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fk-copilot-timeline-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CopilotStore(Path.Combine(directory, "copilot.db"));
            var sessionId = store.CreateSession();
            var server = new ToolStreamingServer();
            using var http = new HttpClient(new ReasoningReplayHandler(server, store, () => sessionId));
            using var client = new OpenAI.Chat.ChatClient("test-agent", new ApiKeyCredential("test-key"),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri("https://example.test/v1"),
                    Transport = new HttpClientPipelineTransport(http)
                }).AsIChatClient();
            var agent = new ChatClientAgent(client, new ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions
                {
                    Tools = [AIFunctionFactory.Create((string query) => $"Found {query}", "SearchCapabilities")]
                }
            });
            var session = await agent.CreateSessionAsync();
            var trace = new CopilotRunTrace();
            await foreach (var update in agent.RunStreamingAsync("Search files", session))
                trace.Apply(update);
            trace.Finish();
            Assert.Equal(["thinking", "assistant", "tools", "thinking", "assistant"],
                trace.Steps.Select(step => step.Kind));
            Assert.Equal("先查找可用文件能力。", trace.Steps[0].Text);
            Assert.Equal("我先搜索。", trace.Steps[1].Text);
            Assert.Single(trace.Steps[2].Calls);
            Assert.True(trace.Steps[2].Finished);
            Assert.Equal("已有搜索结果。", trace.Steps[3].Text);
            Assert.Equal("找到结果。", trace.Steps[^1].Text);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ApprovalResumeReturnsFinalAnswerWithParallelThinkingToolCalls()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fk-copilot-approval-agent-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CopilotStore(Path.Combine(directory, "copilot.db"));
            var sessionId = store.CreateSession();
            var server = new ApprovalStreamingServer();
            using var http = new HttpClient(new ReasoningReplayHandler(server, store, () => sessionId));
            using var client = new OpenAI.Chat.ChatClient("test-agent", new ApiKeyCredential("test-key"),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri("https://example.test/v1"),
                    Transport = new HttpClientPipelineTransport(http)
                }).AsIChatClient();
            var agent = new ChatClientAgent(client, new ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions
                {
                    Tools =
                    [
                        AIFunctionFactory.Create(() => "listed", "ListItems"),
                        new ApprovalRequiredAIFunction(AIFunctionFactory.Create(() => "approved", "ExecuteApprovedPlan"))
                    ]
                }
            });
            var session = await agent.CreateSessionAsync();
            var first = await CopilotEngine.CollectStreamingResponseAsync(
                agent.RunStreamingAsync("List and execute", session), null);
            var approval = Assert.Single(first.Messages.SelectMany(message => message.Contents)
                .OfType<ToolApprovalRequestContent>());
            var resumed = await CopilotEngine.CollectStreamingResponseAsync(
                agent.RunStreamingAsync(new ChatMessage(ChatRole.User, [approval.CreateResponse(true)]), session), null);
            Assert.Contains("已完成", resumed.Text);
            Assert.True(server.SawReasoningOnResume);
            Assert.Contains(2, server.ResumeToolCallCounts);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
    [Fact]
    public async Task OpenAIAdapterReceivesTextBeforeStreamingResponseEnds()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fk-copilot-text-stream-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CopilotStore(Path.Combine(directory, "copilot.db"));
            var session = store.CreateSession();
            using var http = new HttpClient(new ReasoningReplayHandler(new StreamingTextServer(), store, () => session));
            using var client = new OpenAI.Chat.ChatClient("test-stream", new ApiKeyCredential("test-key"),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri("https://example.test/v1"),
                    Transport = new HttpClientPipelineTransport(http)
                }).AsIChatClient();
            var deltas = new List<string>();
            var reasoning = new List<string>();
            await foreach (var update in client.GetStreamingResponseAsync(
                               [new ChatMessage(ChatRole.User, "hello")]))
            {
                reasoning.AddRange(update.Contents.OfType<TextReasoningContent>().Select(content => content.Text));
                if (!string.IsNullOrEmpty(update.Text)) deltas.Add(update.Text);
            }
            Assert.Equal(["**Hello", " world**"], deltas);
            Assert.Equal(["Think first."], reasoning);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task StreamingThinkingIsCapturedAndReplayedForToolFollowUp()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fk-copilot-stream-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var store = new CopilotStore(Path.Combine(directory, "copilot.db"));
            var session = store.CreateSession();
            var server = new StreamingThinkingServer();
            using var client = new HttpClient(new ReasoningReplayHandler(server, store, () => session));
            using (var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/v1/chat/completions")
                   { Content = Json("""{"stream":true,"messages":[{"role":"user","content":"create"}]}""") })
            using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[7];
                while (await stream.ReadAsync(buffer, cancellationToken) > 0) { }
            }
            var captured = Assert.Single(store.LoadReasoning(session));
            Assert.Equal("Think first.", captured.ReasoningContent);
            Assert.Equal("[\"call-create\"]", captured.ToolCallIdsJson);

            using var followUp = await client.PostAsync("https://example.test/v1/chat/completions", Json("""
                {"stream":true,"messages":[{"role":"assistant","content":null,
                "tool_calls":[{"id":"call-create","type":"function","function":{"name":"create","arguments":"{}"}}]},
                {"role":"tool","tool_call_id":"call-create","content":"created"}]}
                """), cancellationToken);
            using var sent = JsonDocument.Parse(Assert.Single(server.FollowUpRequests));
            Assert.Equal("Think first.", sent.RootElement.GetProperty("messages")[0]
                .GetProperty("reasoning_content").GetString());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task OpenAIAdapterReturnsToolResultWithoutLosingThinkingContent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fk-copilot-adapter-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CopilotStore(Path.Combine(directory, "copilot.db"));
            var session = store.CreateSession();
            var server = new ThinkingChatServer();
            using var http = new HttpClient(new ReasoningReplayHandler(server, store, () => session));
            using var client = new OpenAI.Chat.ChatClient("test-thinking", new ApiKeyCredential("test-key"),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri("https://example.test/v1"),
                    Transport = new HttpClientPipelineTransport(http)
                }).AsIChatClient();
            var user = new ChatMessage(ChatRole.User, "Create a file");
            var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "created")] };
            var first = await client.GetResponseAsync([user], options);
            var call = Assert.Single(first.Messages.SelectMany(message => message.Contents)
                .OfType<FunctionCallContent>());
            Assert.Equal("call-create", call.CallId);
            Assert.Equal("I should call the file tool.", Assert.Single(first.Messages
                .SelectMany(message => message.Contents).OfType<TextReasoningContent>()).Text);

            var history = new List<ChatMessage> { user };
            history.AddRange(first.Messages);
            history.Add(new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent(call.CallId, "File created")]));
            var second = await client.GetResponseAsync(history, options);
            Assert.Contains("File created", second.Text);
            Assert.True(server.SawReasoningOnFollowUp);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ThinkingToolCallIsReplayedAfterApprovalAndAfterRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fk-copilot-reasoning-" + Guid.NewGuid().ToString("N"));
        try
        {
            var database = Path.Combine(directory, "copilot.db");
            var store = new CopilotStore(database);
            var session = store.CreateSession();
            var first = new ScriptedServer(
                """
                {"id":"response-1","choices":[{"message":{"role":"assistant","content":null,
                "reasoning_content":"I need the approved tool result.",
                "tool_calls":[{"id":"call-1","type":"function","function":{"name":"create_file","arguments":"{}"}}]}}]}
                """);
            using (var client = new HttpClient(new ReasoningReplayHandler(first, store, () => session)))
            {
                using var response = await client.PostAsync("https://example.test/v1/chat/completions",
                    Json("""{"messages":[{"role":"user","content":"create"}]}"""));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            Assert.Single(store.LoadReasoning(session));

            // A new handler represents an application restart. The stored MAF
            // conversation contains the assistant tool call, but the OpenAI
            // adapter has omitted its provider-specific reasoning field.
            var second = new ScriptedServer(
                """
                {"id":"response-2","choices":[{"message":{"role":"assistant","content":"Created.",
                "reasoning_content":"The tool succeeded."}}]}
                """);
            using (var client = new HttpClient(new ReasoningReplayHandler(second,
                       new CopilotStore(database), () => session)))
            {
                using var response = await client.PostAsync("https://example.test/v1/chat/completions", Json("""
                    {"messages":[{"role":"user","content":"create"},
                    {"role":"assistant","content":null,"tool_calls":[{"id":"call-1","type":"function","function":{"name":"create_file","arguments":"{}"}}]},
                    {"role":"tool","tool_call_id":"call-1","content":"created"}]}
                    """));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            using (var sent = JsonDocument.Parse(Assert.Single(second.Requests)))
                Assert.Equal("I need the approved tool result.", sent.RootElement
                    .GetProperty("messages")[1].GetProperty("reasoning_content").GetString());

            var third = new ScriptedServer("""{"id":"response-3","choices":[{"message":{"role":"assistant","content":"done"}}]}""");
            using (var client = new HttpClient(new ReasoningReplayHandler(third, store, () => session)))
            {
                using var response = await client.PostAsync("https://example.test/v1/chat/completions", Json("""
                    {"messages":[{"role":"assistant","content":null,"tool_calls":[{"id":"call-1"}]},
                    {"role":"tool","tool_call_id":"call-1","content":"created"},
                    {"role":"assistant","content":"Created."},
                    {"role":"user","content":"thanks"}]}
                    """));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            using (var sent = JsonDocument.Parse(Assert.Single(third.Requests)))
            {
                var messages = sent.RootElement.GetProperty("messages");
                Assert.Equal("I need the approved tool result.", messages[0].GetProperty("reasoning_content").GetString());
                Assert.Equal("The tool succeeded.", messages[2].GetProperty("reasoning_content").GetString());
            }

            store.DeleteSession(session);
            Assert.Empty(store.LoadReasoning(session));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ApprovalResumeReplaysReasoningWhenAgentSplitsParallelToolCalls()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fk-copilot-approval-replay-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CopilotStore(Path.Combine(directory, "copilot.db"));
            var session = store.CreateSession();
            store.SaveReasoning(session, "response-with-approval", "", "[\"call-read\",\"call-approved\"]",
                "I need the approved file content and the archive list.");
            var server = new ScriptedServer("""{"id":"done","choices":[{"message":{"role":"assistant","content":"done"}}]}""");
            using var client = new HttpClient(new ReasoningReplayHandler(server, store, () => session));
            using var response = await client.PostAsync("https://example.test/v1/chat/completions", Json("""
                {"messages":[{"role":"assistant","content":null,
                "tool_calls":[{"id":"call-read"}]},
                {"role":"tool","tool_call_id":"call-read","content":"listed"},
                {"role":"assistant","content":null,
                "tool_calls":[{"id":"call-approved"}]},
                {"role":"tool","tool_call_id":"call-approved","content":"approved"}]}
                """));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var sent = JsonDocument.Parse(Assert.Single(server.Requests));
            var messages = sent.RootElement.GetProperty("messages");
            Assert.Equal("I need the approved file content and the archive list.",
                messages[0].GetProperty("reasoning_content").GetString());
            Assert.Equal("I need the approved file content and the archive list.",
                messages[2].GetProperty("reasoning_content").GetString());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ApprovalResumeRemovesEmptyBridgeMessageAndMatchesReorderedCalls()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fk-copilot-approval-bridge-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CopilotStore(Path.Combine(directory, "copilot.db"));
            var session = store.CreateSession();
            store.SaveReasoning(session, "response-before-approval", "", "[\"call-read\",\"call-approved\"]",
                "Both calls belong to this reasoning step.");
            var server = new ScriptedServer("""{"id":"done","choices":[{"message":{"role":"assistant","content":"done"}}]}""");
            using var client = new HttpClient(new ReasoningReplayHandler(server, store, () => session));
            using var response = await client.PostAsync("https://example.test/v1/chat/completions", Json("""
                {"messages":[{"role":"assistant","name":"copilot","content":null},
                {"role":"assistant","name":"copilot",
                "tool_calls":[{"id":"call-approved"},{"id":"call-read"}]}]}
                """));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var sent = JsonDocument.Parse(Assert.Single(server.Requests));
            var assistant = Assert.Single(sent.RootElement.GetProperty("messages").EnumerateArray());
            Assert.Equal("Both calls belong to this reasoning step.",
                assistant.GetProperty("reasoning_content").GetString());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task OldApprovedContentPagesArePrunedFromModelRequests()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fk-copilot-prune-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CopilotStore(Path.Combine(directory, "copilot.db"));
            var session = store.CreateSession();
            var server = new ScriptedServer("""{"id":"done","choices":[{"message":{"role":"assistant","content":"ok"}}]}""");
            using var client = new HttpClient(new ReasoningReplayHandler(server, store, () => session));
            var messages = Enumerable.Range(0, 4).Select(index => new
            {
                role = "tool",
                tool_call_id = $"call-{index}",
                content = JsonSerializer.Serialize(new CapabilityResult(true, "已读取正文",
                    new CopilotContentExtractor.Page(new string((char)('a' + index), 48_000),
                        index * 48_000, (index + 1) * 48_000, index != 3)))
            }).ToArray();
            using var response = await client.PostAsync("https://example.test/v1/chat/completions",
                Json(JsonSerializer.Serialize(new { messages })));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var sent = JsonDocument.Parse(Assert.Single(server.Requests));
            var pages = sent.RootElement.GetProperty("messages").EnumerateArray()
                .Select(message => JsonDocument.Parse(message.GetProperty("content").GetString()!).RootElement.Clone())
                .ToArray();
            Assert.True(pages[0].GetProperty("Data").GetProperty("Pruned").GetBoolean());
            Assert.True(pages[1].GetProperty("Data").GetProperty("Pruned").GetBoolean());
            Assert.Equal(new string('c', 48_000), pages[2].GetProperty("Data").GetProperty("Text").GetString());
            Assert.Equal(new string('d', 48_000), pages[3].GetProperty("Data").GetProperty("Text").GetString());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task OpenAIAdapterAlsoPrunesOldContentPageResults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fk-copilot-adapter-prune-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CopilotStore(Path.Combine(directory, "copilot.db"));
            var session = store.CreateSession();
            var server = new ScriptedServer("""{"id":"done","choices":[{"message":{"role":"assistant","content":"ok"}}]}""");
            using var http = new HttpClient(new ReasoningReplayHandler(server, store, () => session));
            using var client = new OpenAI.Chat.ChatClient("test-agent", new ApiKeyCredential("test-key"),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri("https://example.test/v1"),
                    Transport = new HttpClientPipelineTransport(http)
                }).AsIChatClient();
            var calls = Enumerable.Range(0, 4).Select(index => (AIContent)new FunctionCallContent(
                $"call-{index}", "ExecuteApprovedPlan", new Dictionary<string, object?>
                { ["planId"] = $"plan-{index}" })).ToList();
            var results = Enumerable.Range(0, 4).Select(index => (AIContent)new FunctionResultContent(
                $"call-{index}", JsonSerializer.Serialize(new CapabilityResult(true, "已读取正文",
                    new CopilotContentExtractor.Page(new string((char)('a' + index), 48_000),
                        index * 48_000, (index + 1) * 48_000, index != 3))))).ToList();
            var history = new List<ChatMessage>
            {
                new(ChatRole.User, "Read pages"),
                new(ChatRole.Assistant, calls),
                new(ChatRole.Tool, results)
            };
            await client.GetResponseAsync(history, new ChatOptions
            {
                Tools = [AIFunctionFactory.Create((string planId) => planId, "ExecuteApprovedPlan")]
            });

            using var sent = JsonDocument.Parse(Assert.Single(server.Requests));
            var toolMessages = sent.RootElement.GetProperty("messages").EnumerateArray()
                .Where(message => message.GetProperty("role").GetString() == "tool").ToArray();
            Assert.Equal(4, toolMessages.Length);
            Assert.Contains("Pruned", toolMessages[0].GetProperty("content").GetString());
            Assert.DoesNotContain("Pruned", toolMessages[^1].GetProperty("content").GetString());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    private sealed class ScriptedServer(string responseJson) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(responseJson) };
        }
    }

    private sealed class StreamingThinkingServer : HttpMessageHandler
    {
        private int _requests;
        public List<string> FollowUpRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (++_requests > 1)
            {
                FollowUpRequests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                return new(HttpStatusCode.OK) { Content = Json("{}") };
            }
            const string events = """
                data: {"id":"stream-1","choices":[{"delta":{"reasoning_content":"Think "}}]}

                data: {"id":"stream-1","choices":[{"delta":{"reasoning_content":"first."}}]}

                data: {"id":"stream-1","choices":[{"delta":{"tool_calls":[{"index":0,"id":"call-create"}]}}]}

                data: [DONE]

                """;
            var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(events.Replace("\n", "\r\n"))));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return new(HttpStatusCode.OK) { Content = content };
        }
    }

    private sealed class StreamingTextServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const string events = """
                data: {"id":"reply-1","object":"chat.completion.chunk","created":1,"model":"test-stream","choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"Think first."},"finish_reason":null}]}

                data: {"id":"reply-1","object":"chat.completion.chunk","created":1,"model":"test-stream","choices":[{"index":0,"delta":{"role":"assistant","content":"**Hello"},"finish_reason":null}]}

                data: {"id":"reply-1","object":"chat.completion.chunk","created":1,"model":"test-stream","choices":[{"index":0,"delta":{"content":" world**"},"finish_reason":null}]}

                data: {"id":"reply-1","object":"chat.completion.chunk","created":1,"model":"test-stream","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                data: [DONE]

                """;
            var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(events)));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class ToolStreamingServer : HttpMessageHandler
    {
        private int _requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var first = ++_requests == 1;
            var events = first ? """
                data: {"id":"tool-first","object":"chat.completion.chunk","created":1,"model":"test-agent","choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"先查找可用文件能力。"},"finish_reason":null}]}

                data: {"id":"tool-first","object":"chat.completion.chunk","created":1,"model":"test-agent","choices":[{"index":0,"delta":{"role":"assistant","content":"我先搜索。"},"finish_reason":null}]}

                data: {"id":"tool-first","object":"chat.completion.chunk","created":1,"model":"test-agent","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call-search","type":"function","function":{"name":"SearchCapabilities","arguments":"{\"query\":\"files\"}"}}]},"finish_reason":null}]}

                data: {"id":"tool-first","object":"chat.completion.chunk","created":1,"model":"test-agent","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

                data: [DONE]

                """ : """
                data: {"id":"tool-final","object":"chat.completion.chunk","created":2,"model":"test-agent","choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"已有搜索结果。"},"finish_reason":null}]}

                data: {"id":"tool-final","object":"chat.completion.chunk","created":2,"model":"test-agent","choices":[{"index":0,"delta":{"role":"assistant","content":"找到结果。"},"finish_reason":null}]}

                data: {"id":"tool-final","object":"chat.completion.chunk","created":2,"model":"test-agent","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                data: [DONE]

                """;
            var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(events)));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class ApprovalStreamingServer : HttpMessageHandler
    {
        public bool SawReasoningOnResume { get; private set; }
        public int[] ResumeToolCallCounts { get; private set; } = [];
        private int _requests;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var first = ++_requests == 1;
            if (!first)
            {
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var assistantCalls = document.RootElement.GetProperty("messages").EnumerateArray()
                    .Where(message => message.TryGetProperty("tool_calls", out _)).ToArray();
                ResumeToolCallCounts = assistantCalls.Select(message =>
                    message.GetProperty("tool_calls").GetArrayLength()).ToArray();
                SawReasoningOnResume = assistantCalls.Length > 0 && assistantCalls.All(message =>
                    message.TryGetProperty("reasoning_content", out var reasoning)
                    && reasoning.GetString() == "The two tools should run after approval.");
                if (!SawReasoningOnResume)
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = Json("""{"error":{"message":"The reasoning_content in the thinking mode must be passed back to the API."}}""")
                    };
            }
            var events = first ? """
                data: {"id":"approval-first","object":"chat.completion.chunk","created":1,"model":"test-agent","choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"The two tools should run after approval."},"finish_reason":null}]}

                data: {"id":"approval-first","object":"chat.completion.chunk","created":1,"model":"test-agent","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call-list","type":"function","function":{"name":"ListItems","arguments":"{}"}},{"index":1,"id":"call-approve","type":"function","function":{"name":"ExecuteApprovedPlan","arguments":"{}"}}]},"finish_reason":null}]}

                data: {"id":"approval-first","object":"chat.completion.chunk","created":1,"model":"test-agent","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

                data: [DONE]

                """ : """
                data: {"id":"approval-final","object":"chat.completion.chunk","created":2,"model":"test-agent","choices":[{"index":0,"delta":{"role":"assistant","content":"已完成。"},"finish_reason":null}]}

                data: {"id":"approval-final","object":"chat.completion.chunk","created":2,"model":"test-agent","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                data: [DONE]

                """;
            var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(events)));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    private sealed class ThinkingChatServer : HttpMessageHandler
    {
        private int _requests;
        public bool SawReasoningOnFollowUp { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests++;
            if (_requests == 1)
                return new(HttpStatusCode.OK)
                {
                    Content = Json("""
                        {"id":"thinking-1","object":"chat.completion","created":1,"model":"test-thinking",
                        "choices":[{"index":0,"message":{"role":"assistant","content":null,
                        "reasoning_content":"I should call the file tool.",
                        "tool_calls":[{"id":"call-create","type":"function",
                        "function":{"name":"file_tool","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}
                        """)
                };
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            SawReasoningOnFollowUp = body.RootElement.GetProperty("messages")
                .EnumerateArray().Any(message => message.TryGetProperty("tool_calls", out _)
                    && message.TryGetProperty("reasoning_content", out var reasoning)
                    && reasoning.GetString() == "I should call the file tool.");
            return SawReasoningOnFollowUp
                ? new(HttpStatusCode.OK)
                {
                    Content = Json("""
                        {"id":"thinking-2","object":"chat.completion","created":2,"model":"test-thinking",
                        "choices":[{"index":0,"message":{"role":"assistant","content":"File created."},
                        "finish_reason":"stop"}]}
                        """)
                }
                : new(HttpStatusCode.BadRequest)
                {
                    Content = Json("""{"error":{"message":"reasoning_content missing"}}""")
                };
        }
    }
}
