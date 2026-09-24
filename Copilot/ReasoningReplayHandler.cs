using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MacExplorer.Copilot;

/// <summary>
/// Restores reasoning_content for OpenAI-compatible thinking models. The OpenAI
/// chat adapter reads this provider field but currently drops it from later
/// requests; the replay data remains private to the local Copilot session.
/// </summary>
internal sealed class ReasoningReplayHandler(
    HttpMessageHandler inner, CopilotStore store, Func<string> sessionId) : DelegatingHandler(inner)
{
    private const int RetainedContentBytes = 128 * 1024;
    private const string PrunedContent = "[先前已批准的正文已从模型上下文清理；如需重看，请重新预览 file.content 并申请批准。]";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!IsChatCompletion(request.RequestUri))
            return await base.SendAsync(request, cancellationToken);

        await RestoreRequestAsync(request, cancellationToken);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode && response.Content?.Headers.ContentType?.MediaType == "text/event-stream")
        {
            var original = response.Content;
            var stream = await original.ReadAsStreamAsync(cancellationToken);
            var replacement = new StreamContent(new ReasoningCaptureStream(stream, original, store, sessionId()));
            CopyHeaders(original.Headers, replacement.Headers);
            response.Content = replacement;
            return response;
        }
        if (!response.IsSuccessStatusCode || response.Content == null
            || response.Content.Headers.ContentType?.MediaType != "application/json")
            return response;

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        CaptureResponse(bytes);
        ReplaceContent(response, bytes);
        return response;
    }

    private static bool IsChatCompletion(Uri? uri)
        => uri?.AbsolutePath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) == true;

    private async Task RestoreRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content == null) return;
        var original = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        JsonNode? root;
        try { root = JsonNode.Parse(original); }
        catch (JsonException) { return; }
        if (root is not JsonObject body || body["messages"] is not JsonArray messages) return;

        var modified = false;
        // The approval bridge can serialize an informational-only assistant
        // message as { role, name, content: null }. It has no model output or
        // tool call and DeepSeek rejects it in a thinking-mode history.
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject message || Text(message["role"]) != "assistant"
                || message.ContainsKey("tool_calls") || message.ContainsKey("reasoning_content")
                || message.Any(field => field.Key is not ("role" or "name" or "content"))) continue;
            var content = message["content"];
            if (content != null && (content is not JsonValue || ContentText(content).Length != 0)) continue;
            messages.RemoveAt(i);
            modified = true;
        }
        modified |= PruneOldContentPages(messages);

        var saved = store.LoadReasoning(sessionId()).Select(item => new ReplayEntry(
            item.Content, JsonSerializer.Deserialize<string[]>(item.ToolCallIdsJson) ?? [],
            item.ReasoningContent)).ToArray();
        var used = new bool[saved.Length];
        foreach (var node in messages)
        {
            if (node is not JsonObject message || Text(message["role"]) != "assistant"
                || message.ContainsKey("reasoning_content")) continue;
            var callIds = ToolCallIds(message);
            var content = ContentText(message["content"]);
            for (var i = 0; i < saved.Length; i++)
            {
                // MAF can split a model response that contains several tool calls when
                // one of them waits for approval. The resumed request then carries
                // only a subset of the original calls. DeepSeek still requires the
                // reasoning from the original response on that assistant message.
                if ((callIds.Length == 0 && used[i]) || (callIds.Length > 0
                        ? !callIds.All(id => saved[i].ToolCallIds.Contains(id, StringComparer.Ordinal))
                        : saved[i].ToolCallIds.Length != 0 || saved[i].Content != content)) continue;
                message["reasoning_content"] = saved[i].ReasoningContent;
                if (callIds.Length == 0) used[i] = true;
                modified = true;
                break;
            }
        }
        if (!modified) return;
        var replacement = new ByteArrayContent(Encoding.UTF8.GetBytes(body.ToJsonString()));
        CopyHeaders(request.Content.Headers, replacement.Headers);
        request.Content.Dispose();
        request.Content = replacement;
    }

    private void CaptureResponse(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("message", out var message)
                || !message.TryGetProperty("reasoning_content", out var reasoning)
                || reasoning.ValueKind != JsonValueKind.String) return;
            var responseId = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()! : Guid.NewGuid().ToString("N");
            var content = message.TryGetProperty("content", out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            var callIds = message.TryGetProperty("tool_calls", out var calls)
                && calls.ValueKind == JsonValueKind.Array
                ? calls.EnumerateArray().Select(call =>
                    call.ValueKind == JsonValueKind.Object
                    && call.TryGetProperty("id", out var callId)
                    && callId.ValueKind == JsonValueKind.String ? callId.GetString() ?? "" : "").ToArray()
                : [];
            store.SaveReasoning(sessionId(), responseId, content,
                JsonSerializer.Serialize(callIds), reasoning.GetString()!);
        }
        catch (JsonException) { /* Keep the provider response intact. */ }
    }

    private static string[] ToolCallIds(JsonObject message)
        => message["tool_calls"] is JsonArray calls
            ? calls.Select(call => Text(call?["id"])).ToArray() : [];

    private static string ContentText(JsonNode? content)
        => content is JsonArray parts
            ? string.Concat(parts.Select(part => Text(part?["text"]))) : Text(content);

    private static string Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static bool PruneOldContentPages(JsonArray messages)
    {
        var retainedBytes = 0;
        var modified = false;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject message || Text(message["role"]) != "tool") continue;
            var content = ContentText(message["content"]);
            var replacement = PrunedContentResult(content);
            if (replacement == null) continue;
            var bytes = Encoding.UTF8.GetByteCount(content);
            if (retainedBytes + bytes <= RetainedContentBytes)
            {
                retainedBytes += bytes;
                continue;
            }
            message["content"] = replacement;
            modified = true;
        }
        return modified;
    }

    private static string? PrunedContentResult(string content)
    {
        JsonObject? result;
        try { result = JsonNode.Parse(content) as JsonObject; }
        catch (JsonException) { return null; }
        if (result == null) return null;
        if (result["Data"] is JsonObject page && Text(page["Kind"]) == "file-content-page")
        {
            page["Text"] = PrunedContent;
            page["Pruned"] = true;
            return result.ToJsonString();
        }
        // Older sessions stored an excerpt directly as Data instead of a page.
        if (result["Data"] is JsonValue && Text(result["Message"]).StartsWith("已提取 ", StringComparison.Ordinal))
        {
            result["Data"] = PrunedContent;
            return result.ToJsonString();
        }
        return null;
    }

    private static void ReplaceContent(HttpResponseMessage response, byte[] bytes)
    {
        var replacement = new ByteArrayContent(bytes);
        CopyHeaders(response.Content!.Headers, replacement.Headers);
        response.Content.Dispose();
        response.Content = replacement;
    }

    private static void CopyHeaders(HttpContentHeaders source, HttpContentHeaders target)
    {
        foreach (var header in source)
            if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                target.TryAddWithoutValidation(header.Key, header.Value);
    }

    private sealed record ReplayEntry(string Content, string[] ToolCallIds, string ReasoningContent);

    private sealed class ReasoningCaptureStream(
        Stream inner, HttpContent original, CopilotStore store, string sessionId) : Stream
    {
        private readonly List<byte> _event = [];
        private readonly StringBuilder _content = new();
        private readonly StringBuilder _reasoning = new();
        private readonly SortedDictionary<int, string> _callIds = new();
        private string? _responseId;
        private bool _saved;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Capture(buffer.AsSpan(offset, read), read == 0);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer, offset, count, cancellationToken);
            Capture(buffer.AsSpan(offset, read), read == 0);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            Capture(buffer.Span[..read], read == 0);
            return read;
        }

        private void Capture(ReadOnlySpan<byte> bytes, bool completed)
        {
            foreach (var value in bytes)
            {
                _event.Add(value);
                var count = _event.Count;
                if (!(count >= 2 && _event[^2] == '\n' && _event[^1] == '\n')
                    && !(count >= 4 && _event[^4] == '\r' && _event[^3] == '\n'
                         && _event[^2] == '\r' && _event[^1] == '\n')) continue;
                ParseEvent(Encoding.UTF8.GetString(_event.ToArray()));
                _event.Clear();
            }
            if (completed)
            {
                if (_event.Count > 0) ParseEvent(Encoding.UTF8.GetString(_event.ToArray()));
                Save();
            }
        }

        private void ParseEvent(string raw)
        {
            var data = string.Concat(raw.Split('\n')
                .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                .Select(line => line[5..].Trim().TrimEnd('\r')));
            if (data == "[DONE]") { Save(); return; }
            if (data.Length == 0) return;
            try
            {
                using var document = JsonDocument.Parse(data);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    _responseId = id.GetString();
                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() == 0 || choices[0].ValueKind != JsonValueKind.Object
                    || !choices[0].TryGetProperty("delta", out var delta)
                    || delta.ValueKind != JsonValueKind.Object) return;
                if (delta.TryGetProperty("reasoning_content", out var reasoning)
                    && reasoning.ValueKind == JsonValueKind.String)
                    _reasoning.Append(reasoning.GetString());
                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    _content.Append(content.GetString());
                if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                    foreach (var call in calls.EnumerateArray())
                        if (call.ValueKind == JsonValueKind.Object
                            && call.TryGetProperty("index", out var index) && index.TryGetInt32(out var position)
                            && call.TryGetProperty("id", out var callId) && callId.ValueKind == JsonValueKind.String)
                            _callIds[position] = callId.GetString()!;
            }
            catch (JsonException) { /* Malformed provider events must not interrupt the reply. */ }
        }

        private void Save()
        {
            if (_saved || _reasoning.Length == 0) return;
            _saved = true;
            store.SaveReasoning(sessionId, _responseId ?? Guid.NewGuid().ToString("N"),
                _content.ToString(), JsonSerializer.Serialize(_callIds.Values), _reasoning.ToString());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) original.Dispose();
            base.Dispose(disposing);
        }
    }
}
