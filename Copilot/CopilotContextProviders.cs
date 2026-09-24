using Avalonia.Threading;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Microsoft.Agents.AI;

namespace MacExplorer.Copilot;

internal sealed class WorkspaceContextProvider(Func<FileListViewModel?> activePane) : AIContextProvider
{
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var pane = activePane();
            if (pane == null) return "当前没有活动文件窗格。";
            var lines = new List<string>();
            if (pane.OwnerWindow is MainWindow { DataContext: MainWindowViewModel workspace })
                lines.Add($"当前窗口有 {workspace.Tabs.Count} 个标签页，窗格布局为 {workspace.PaneLayout}。");
            lines.Add($"当前活动窗格路径：{pane.CurrentPath}。");
            // Read one extra item to disclose truncation without enumerating a large selection.
            var selected = pane.SelectedEntries.Take(31).Select(entry => entry.FullPath).ToArray();
            lines.Add(selected.Length == 0 ? "当前无选中项。" :
                (selected.Length > 30 ? "当前选中路径（仅前 30 项，非全部）：" : "当前选中路径：")
                + string.Join("；", selected.Take(30)));
            lines.Add(pane.IsSearchMode
                ? $"当前搜索：{pane.SearchQuery}。前 30 项结果：{string.Join("；", pane.Entries.Take(30).Select(entry => entry.FullPath))}。"
                : "当前窗格未显示搜索结果。");
            return string.Join("\n", lines);
        });
        return new AIContext { Instructions = snapshot };
    }
}

internal sealed class ArtifactIndexContextProvider(CopilotStore store, Func<string> sessionId) : AIContextProvider
{
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        var items = store.ListArtifacts(sessionId(), 12);
        var instruction = items.Count == 0 ? "本会话暂无中间产物。" :
            "本会话产物 ID 与类型：" + string.Join("；", items.Select(item => $"{item.Id} ({item.Kind})"));
        return ValueTask.FromResult(new AIContext { Instructions = instruction });
    }
}

internal sealed class ApprovedContentContextProvider(CopilotStore store, Func<string> sessionId) : AIContextProvider
{
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        var excerpts = store.ListArtifacts(sessionId(), 30)
            .Where(item => item.Kind == "content-excerpt")
            .Select(item => item.Id).ToArray();
        var instruction = excerpts.Length == 0 ? "本会话尚无已批准的文件正文。" :
            $"本会话曾批准 {excerpts.Length} 份内容摘录，产物 ID：{string.Join("、", excerpts)}。重新发送正文必须再次走 file.content 审批。";
        return ValueTask.FromResult(new AIContext { Instructions = instruction });
    }
}

internal sealed class PendingPlanContextProvider(Func<IReadOnlyList<CapabilityPlan>> plans) : AIContextProvider
{
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        var pending = plans().Take(10).Select(plan => $"{plan.Id}: {plan.Summary}").ToArray();
        return ValueTask.FromResult(new AIContext
        {
            Instructions = "本轮待审批计划：" + (pending.Length > 0 ? string.Join("；", pending) : "无")
        });
    }
}
