using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Copilot;
using MacExplorer.Models;
using MacExplorer.Views;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaFact]
    public async Task CopilotPanelAndHistoryFitTheMinimumWindow()
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(1);
        var window = fixture.Window;
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        var panel = window.FindControl<Border>("CopilotPanel")!;
        panel.IsVisible = true;

        void AssertFits()
        {
            window.UpdateLayout();
            var topLeft = panel.TranslatePoint(new Point(), window);
            Assert.NotNull(topLeft);
            Assert.True(topLeft.Value.X >= 0 && topLeft.Value.Y >= 0);
            Assert.True(topLeft.Value.X + panel.Bounds.Width <= window.Bounds.Width);
            Assert.True(topLeft.Value.Y + panel.Bounds.Height <= window.Bounds.Height);
        }

        AssertFits();
        window.FindControl<Border>("CopilotHistoryPane")!.IsVisible = true;
        panel.Width = 664;
        AssertFits();
    }

    [AvaloniaFact]
    public async Task StopButtonCancelsStreamingAndStartsASeparateSession()
    {
        var root = Path.Combine(Path.GetTempPath(), "fk-copilot-stop-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CopilotStore(Path.Combine(root, "copilot.db"));
            using var theme = new FastListTestTheme();
            await using var fixture = await TabCacheFixture.CreateAsync(1,
                services => services.AddSingleton(store));
            var engine = new CopilotEngine(null!, null!, null!, store, null!, () => null);
            typeof(MainWindow).GetField("_copilot", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(fixture.Window, engine);
            var original = engine.SessionId;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = RunCopilotWindowAction(fixture.Window, async (_, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return new CopilotReply("", null, false);
            }, true);
            await started.Task;
            var stop = fixture.Window.FindControl<Button>("CopilotStopButton")!;
            Assert.True(stop.IsVisible);
            Assert.True(stop.IsEnabled);
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await task;
            Assert.NotEqual(original, engine.SessionId);
            Assert.False(stop.IsVisible);
            Assert.True(fixture.Window.FindControl<Button>("CopilotSendButton")!.IsEnabled);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task ClosingCopilotCancelsStreamingAndReleasesSessionOwnership()
    {
        var root = Path.Combine(Path.GetTempPath(), "fk-copilot-close-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CopilotStore(Path.Combine(root, "copilot.db"));
            using var theme = new FastListTestTheme();
            await using var fixture = await TabCacheFixture.CreateAsync(1,
                services => services.AddSingleton(store));
            var engine = new CopilotEngine(null!, null!, null!, store, null!, () => null);
            typeof(MainWindow).GetField("_copilot", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(fixture.Window, engine);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = RunCopilotWindowAction(fixture.Window, async (_, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return new CopilotReply("", null, false);
            }, true);
            typeof(MainWindow).GetField("_copilotRunTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(fixture.Window, task);
            await started.Task;
            await (Task)typeof(MainWindow).GetMethod("CloseCopilotAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(fixture.Window, null)!;
            Assert.True(task.IsCompleted);
            Assert.Throws<ObjectDisposedException>(engine.PrepareForSend);
            Assert.True(store.TryAcquireSession(engine.SessionId, "another-window"));
            store.ReleaseSession(engine.SessionId, "another-window");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task CopilotApprovalRemainsRejectableWhenTranscriptAndHistoryReadsFail()
    {
        var root = Path.Combine(Path.GetTempPath(), "fk-copilot-window-" + Guid.NewGuid().ToString("N"));
        try
        {
            var database = Path.Combine(root, "copilot.db");
            var store = new CopilotStore(database);
            using var theme = new FastListTestTheme();
            await using var fixture = await TabCacheFixture.CreateAsync(1,
                services => services.AddSingleton(store));
            var registry = new AppCapabilityRegistry(fixture.Files, null!, null!, null!, null!, null!, null!,
                null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);
            var engine = new CopilotEngine(registry, null!, null!, store, null!, () => fixture.Model.SelectedTab!.FileList);
            typeof(MainWindow).GetField("_copilot", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(fixture.Window, engine);
            fixture.Window.FindControl<Border>("CopilotPanel")!.IsVisible = true;
            var history = fixture.Window.FindControl<Border>("CopilotHistoryPane")!;
            history.IsVisible = true;
            var existingRow = new TextBlock { Text = "previous session" };
            fixture.Window.FindControl<StackPanel>("CopilotHistoryList")!.Children.Add(existingRow);
            using var preview = JsonDocument.Parse(await engine.PreviewOperationAsync("file.rating",
                JsonSerializer.Serialize(new { path = fixture.Files.HomeDirectory + "/0000.txt", rating = 3 })));
            var request = new ToolApprovalRequestContent("approval", new FunctionCallContent("call", "ExecuteApprovedPlan",
                new Dictionary<string, object?> { ["planId"] = preview.RootElement.GetProperty("Id").GetString() }));
            var response = new AgentResponse(new ChatMessage(ChatRole.Assistant, [request]));
            await (Task<CopilotReply>)typeof(CopilotEngine).GetMethod("FinishAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(engine, [response, CancellationToken.None])!;

            // Make both the error transcript write and final history read fail immediately.
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "ALTER TABLE turns RENAME TO unavailable_turns; ALTER TABLE sessions RENAME TO unavailable_sessions;";
                command.ExecuteNonQuery();
            }
            await RunCopilotWindowAction(fixture.Window, _ => throw new IOException("simulated persistence failure"));
            Assert.True(engine.NeedsApproval);
            var approval = fixture.Window.FindControl<Border>("CopilotApprovalCard")!;
            Assert.True(approval.IsVisible);
            Assert.True(fixture.Window.FindControl<Button>("CopilotApproveButton")!.IsEnabled);
            Assert.False(fixture.Window.FindControl<TextBox>("CopilotInput")!.IsEnabled);
            Assert.Contains(existingRow, fixture.Window.FindControl<StackPanel>("CopilotHistoryList")!.Children);

            fixture.Window.FindControl<Button>("CopilotApproveButton")!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(engine.NeedsApproval);
            Assert.True(approval.IsVisible);

            // Exercise the real rejection button; no model is configured in this fixture.
            approval.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "拒绝"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(engine.NeedsApproval);
            Assert.False(approval.IsVisible);
            Assert.True(fixture.Window.FindControl<TextBox>("CopilotInput")!.IsEnabled);
            Assert.True(fixture.Window.FindControl<Button>("CopilotSendButton")!.IsEnabled);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task CopilotFileListPagesKeepStableOrderAndSavedCandidateContracts()
    {
        var root = Path.Combine(Path.GetTempPath(), "fk-copilot-candidates-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = new FakeFileService(root);
            var expected = Enumerable.Range(0, 401).Select(index => $"{root}/{index:D4}.txt").ToArray();
            foreach (var path in expected.Reverse())
                files.Seed(new FileSystemEntry { FullPath = path, Name = Path.GetFileName(path) });
            var registry = new AppCapabilityRegistry(files, null!, null!, null!, null!, null!, null!,
                null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);
            var store = new CopilotStore(Path.Combine(root, "copilot.db"));
            var engine = new CopilotEngine(registry, null!, null!, store, null!, () => null);
            var collected = new List<string>();
            var offset = 0;
            do
            {
                using var reply = JsonDocument.Parse(await engine.CallReadOnlyAsync("file.list",
                    JsonSerializer.Serialize(new { path = root, offset })));
                var id = reply.RootElement.GetProperty("artifactId").GetString()!;
                using var artifact = JsonDocument.Parse(store.GetArtifact(engine.SessionId, id)!.Value);
                Assert.Equal(reply.RootElement.GetProperty("result").GetRawText(), artifact.RootElement.GetRawText());
                var data = artifact.RootElement.GetProperty("Data");
                var paths = data.EnumerateArray().Select(item => item.GetProperty("FullPath").GetString()!).ToArray();
                collected.AddRange(paths);
                engine.SaveClassificationProposal(id, JsonSerializer.Serialize(new[] { new { name = "文档", paths } }));
                var page = artifact.RootElement.GetProperty("Page");
                if (!page.GetProperty("HasMore").GetBoolean()) break;
                offset = page.GetProperty("NextOffset").GetInt32();
            } while (true);
            Assert.Equal(expected, collected);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static Task RunCopilotWindowAction(MainWindow window,
        Func<Action<AgentResponseUpdate>, Task<CopilotReply>> action) =>
        RunCopilotWindowAction(window, (onUpdate, _) => action(onUpdate), true);

    private static Task RunCopilotWindowAction(MainWindow window,
        Func<Action<AgentResponseUpdate>, CancellationToken, Task<CopilotReply>> action, bool allowStop) =>
        (Task)typeof(MainWindow).GetMethod("RunCopilotAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [action, allowStop])!;
}
