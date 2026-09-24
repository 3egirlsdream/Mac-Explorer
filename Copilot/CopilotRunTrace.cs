using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MacExplorer.Copilot;

public sealed class CopilotTraceStep
{
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public List<CopilotToolCall> Calls { get; set; } = [];
    public bool Finished { get; set; }
}

public sealed class CopilotToolCall
{
    public string CallId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Result { get; set; } = "";
    public bool Finished { get; set; }
    public bool Failed { get; set; }
    public bool NeedsApproval { get; set; }
}

/// <summary>Preserves the order of model messages and each model-produced batch of tool calls.</summary>
public sealed class CopilotRunTrace
{
    private string? _messageId;
    private CopilotTraceStep? _text;
    private CopilotTraceStep? _batch;
    private CopilotTraceStep? _thinking;

    public List<CopilotTraceStep> Steps { get; } = [];

    public CopilotRunTrace() => StartThinking();

    public void Apply(AgentResponseUpdate update)
    {
        var results = update.Contents.OfType<FunctionResultContent>().ToArray();
        if (results.Length > 0)
        {
            _text = null;
            foreach (var result in results)
                CompleteCall(result);
            return;
        }
        if (update.Role != null && update.Role != ChatRole.Assistant) return;
        if (update.MessageId != null && update.MessageId != _messageId)
        {
            _messageId = update.MessageId;
            _text = null;
            _batch = null;
        }
        foreach (var reasoning in update.Contents.OfType<TextReasoningContent>())
        {
            if (string.IsNullOrEmpty(reasoning.Text)) continue;
            if (_thinking == null) StartThinking();
            _thinking!.Text += reasoning.Text;
        }
        if (!string.IsNullOrEmpty(update.Text))
        {
            EndThinking();
            _text ??= Add("assistant");
            _text.Text += update.Text;
            _batch = null;
        }
        foreach (var call in update.Contents.OfType<FunctionCallContent>())
            AddCall(call);
        foreach (var approval in update.Contents.OfType<ToolApprovalRequestContent>())
        {
            if (approval.ToolCall is FunctionCallContent call)
            {
                AddCall(call);
                var pending = FindCall(call.CallId);
                if (pending != null) pending.NeedsApproval = true;
            }
        }
    }

    public void Finish(bool failed = false)
    {
        if (_thinking != null)
        {
            if (string.IsNullOrEmpty(_thinking.Text))
                _thinking.Text = failed ? "处理未完成" : "思考完成";
            _thinking.Finished = true;
            _thinking = null;
        }
        if (failed)
            foreach (var call in Steps.SelectMany(step => step.Calls)
                         .Where(call => !call.Finished && !call.NeedsApproval))
            {
                call.Finished = true;
                call.Failed = true;
                call.Result = "调用中断";
            }
        foreach (var step in Steps.Where(step => step.Kind == "tools"))
            step.Finished = step.Calls.All(call => call.Finished);
    }

    private void AddCall(FunctionCallContent call)
    {
        if (string.IsNullOrEmpty(call.CallId)) return;
        EndThinking();
        _text = null;
        var existing = FindCall(call.CallId);
        if (existing != null) return;
        _batch ??= Add("tools");
        _batch.Calls.Add(new CopilotToolCall
        {
            CallId = call.CallId,
            Name = call.Name,
            Detail = DescribeArguments(call)
        });
    }

    private void CompleteCall(FunctionResultContent result)
    {
        if (string.IsNullOrEmpty(result.CallId)) return;
        var call = FindCall(result.CallId);
        if (call == null)
        {
            _batch ??= Add("tools");
            call = new CopilotToolCall { CallId = result.CallId, Name = "已批准操作" };
            _batch.Calls.Add(call);
        }
        (call.Result, call.Failed) = DescribeResult(result.Result);
        call.Finished = true;
        call.NeedsApproval = false;
        var batch = Steps.First(step => step.Calls.Contains(call));
        batch.Finished = batch.Calls.All(item => item.Finished);
        _batch = null;
        if (batch.Finished) StartThinking();
    }

    private CopilotToolCall? FindCall(string callId)
        => Steps.SelectMany(step => step.Calls)
            .FirstOrDefault(call => call.CallId == callId);

    private void StartThinking()
    {
        if (_thinking != null) return;
        _thinking = Add("thinking");
        _text = null;
        _batch = null;
    }

    private void EndThinking()
    {
        if (_thinking == null) return;
        if (string.IsNullOrEmpty(_thinking.Text)) _thinking.Text = "思考完成";
        _thinking.Finished = true;
        _thinking = null;
    }

    private CopilotTraceStep Add(string kind)
    {
        var step = new CopilotTraceStep { Kind = kind };
        Steps.Add(step);
        return step;
    }

    private static string DescribeArguments(FunctionCallContent call)
    {
        if (call.Arguments == null) return "";
        var values = call.Arguments;
        var pieces = new List<string>();
        if (values.TryGetValue("id", out var id)) pieces.Add($"{id}");
        if (values.TryGetValue("query", out var query) && !string.IsNullOrWhiteSpace(query?.ToString()))
            pieces.Add($"{query}");
        if (values.TryGetValue("argumentsJson", out var argumentsJson))
        {
            var json = argumentsJson switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => null
            };
            if (json != null)
            {
                try
                {
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (document.RootElement.TryGetProperty("path", out var path)
                            && path.ValueKind == JsonValueKind.String)
                            pieces.Add(path.GetString() ?? "");
                        if (document.RootElement.TryGetProperty("query", out var nestedQuery)
                            && nestedQuery.ValueKind == JsonValueKind.String)
                            pieces.Add(nestedQuery.GetString() ?? "");
                    }
                }
                catch (JsonException) { }
            }
        }
        if (pieces.Count > 0) return string.Join(" · ", pieces);
        if (values.TryGetValue("artifactId", out var artifact)) return $"产物 {artifact}";
        if (values.TryGetValue("planId", out var plan)) return $"计划 {plan}";
        return "";
    }

    private static (string Summary, bool Failed) DescribeResult(object? value)
    {
        var raw = value?.ToString() ?? "";
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
                return ($"返回 {root.GetArrayLength()} 项", false);
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("result", out var nested)) root = nested;
                var failed = root.TryGetProperty("Success", out var success)
                             && success.ValueKind == JsonValueKind.False;
                if (root.TryGetProperty("Message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                    return (message.GetString() ?? "", failed);
                if (root.TryGetProperty("Summary", out var summary)
                    && summary.ValueKind == JsonValueKind.String)
                    return (summary.GetString() ?? "", false);
                if (root.TryGetProperty("artifactId", out _)) return ("已保存产物", false);
                return ("已返回结果", false);
            }
        }
        catch (JsonException) { }
        if (string.IsNullOrWhiteSpace(raw)) return ("已完成", false);
        return (raw.Length > 140 ? raw[..140] + "…" : raw, raw.Contains("error", StringComparison.OrdinalIgnoreCase));
    }
}
