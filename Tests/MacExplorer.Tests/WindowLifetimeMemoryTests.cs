using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Platforms.MacOS;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Subscriptions;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Area", "Memory")]
public sealed class WindowLifetimeMemoryTests : FileListViewModelTestBase
{
    private sealed class StubSubscriptionStore : ISubscriptionStore
    {
        public event Action? TransactionsChanged { add { } remove { } }
        public void Start() { }
        public Task<SubscriptionSnapshot> CheckAsync(CancellationToken token) =>
            Task.FromResult(new SubscriptionSnapshot(SubscriptionState.Unsubscribed));
        public Task<SubscriptionProduct> LoadProductAsync(CancellationToken token) =>
            Task.FromResult(new SubscriptionProduct("¥9.90", true, 7));
        public Task<PurchaseOutcome> PurchaseAsync(CancellationToken token) =>
            Task.FromResult(PurchaseOutcome.Cancelled);
        public Task RestoreAsync(CancellationToken token) => Task.CompletedTask;
        public Task ManageAsync(CancellationToken token) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class ToggleableTagStore : IFileTagStore
    {
        public bool FailWrites { get; set; }
        public Dictionary<string, IReadOnlyList<NativeFileTag>> Files { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<NativeFileTag>> ReadAsync(string filePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NativeFileTag>>(Files.TryGetValue(filePath, out var tags) ? tags : []);

        public Task WriteAsync(string filePath, IReadOnlyList<NativeFileTag> tags, CancellationToken cancellationToken = default)
        {
            if (FailWrites)
                throw new IOException("Tag store temporarily unavailable");
            Files[filePath] = tags.ToArray();
            return Task.CompletedTask;
        }
    }

    private static int GetEventHandlerCount(object target, string eventFieldName)
    {
        var field = target.GetType().GetField(eventFieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        var multicast = (Delegate?)field?.GetValue(target);
        return multicast?.GetInvocationList().Length ?? 0;
    }

    private static async Task CloseWindowAndWaitAsync(Window window)
    {
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!closed && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(closed, "Expected window to finish closing.");
    }

    private sealed class MinimalMainWindowServices : IDisposable
    {
        private readonly IServiceProvider _previous = App.Services;
        private readonly ServiceProvider _services;

        public MinimalMainWindowServices()
        {
            var files = new FakeFileService("/tmp/window-lifetime-tests");
            _services = new ServiceCollection()
                .AddSingleton<ISettingsService>(new StartupSettings())
                .AddSingleton<IBackgroundTaskManager, BackgroundTaskManager>()
                .AddSingleton<IGlobalSearchScopeService, GlobalSearchScopeService>()
                .AddSingleton<IDragDropBridge>(new MacDragDropBridge(files, new DirectoryChangeNotifier()))
                .BuildServiceProvider();
            App.Services = _services;
        }

        public void Dispose()
        {
            App.Services = _previous;
            _services.Dispose();
        }
    }

    [AvaloniaFact]
    [Trait("Kind", "UI")]
    public async Task ReleaseClosedMainWindowPromotesSurvivingWindowAndClearsLast()
    {
        using var theme = new FastListTestTheme();
        using var services = new MinimalMainWindowServices();
        var first = new MainWindow { Width = 640, Height = 480 };
        var second = new MainWindow { Width = 640, Height = 480 };
        typeof(MainWindow).GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(first, true);
        typeof(MainWindow).GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(second, true);
        var tool = new Window { Width = 320, Height = 240 };
        Window? currentMain = first;
        var openWindows = new List<Window>();
        try
        {
            first.Show();
            second.Show();
            tool.Show();
            Dispatcher.UIThread.RunJobs();
            openWindows.AddRange([first, second, tool]);

            // Closing a non-MainWindow target does not alter desktop.MainWindow.
            openWindows.Remove(tool);
            currentMain = App.ResolveNextMainWindow(currentMain, openWindows, tool);
            Assert.Same(first, currentMain);

            // Closing the current desktop.MainWindow promotes a surviving MainWindow.
            openWindows.Remove(first);
            currentMain = App.ResolveNextMainWindow(currentMain, openWindows, first);
            Assert.Same(second, currentMain);
            Assert.Same(second, App.ResolveLiveWindow(openWindows, currentMain));

            // Closing the last MainWindow clears desktop.MainWindow instead of retaining it.
            openWindows.Remove(second);
            currentMain = App.ResolveNextMainWindow(currentMain, openWindows, second);
            Assert.Null(currentMain);
            Assert.Null(App.ResolveLiveWindow(openWindows, currentMain));

            // Even if desktop.MainWindow still holds a stale closed window, ResolveLiveWindow ignores it.
            currentMain = first;
            Assert.Null(App.ResolveLiveWindow(openWindows, currentMain));
            openWindows.Add(tool);
            Assert.Same(tool, App.ResolveLiveWindow(openWindows, currentMain));
        }
        finally
        {
            if (tool.IsVisible) tool.Close();
            if (first.IsVisible) await CloseWindowAndWaitAsync(first);
            if (second.IsVisible) await CloseWindowAndWaitAsync(second);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    [Trait("Kind", "UI")]
    public void AppWindowOpenedSubscriptionIsIdempotentAcrossHideAndShowCycles()
    {
        using var subscription = new SubscriptionService(new StubSubscriptionStore(), enabled: true);
        var window = new AppWindow(subscription) { Width = 320, Height = 240 };
        var openedCount = 0;
        window.Opened += (_, _) => openedCount++;

        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, GetEventHandlerCount(subscription, "Changed"));

        window.Hide();
        Dispatcher.UIThread.RunJobs();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(openedCount >= 2);
        Assert.Equal(1, GetEventHandlerCount(subscription, "Changed"));

        window.Hide();
        Dispatcher.UIThread.RunJobs();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, GetEventHandlerCount(subscription, "Changed"));

        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, GetEventHandlerCount(subscription, "Changed"));
    }

    [AvaloniaFact]
    [Trait("Kind", "UI")]
    public async Task MainWindowOpenedFeatureAccessSubscriptionIsIdempotentAndDetachesOnClose()
    {
        using var theme = new FastListTestTheme();
        using var services = new MinimalMainWindowServices();
        var previousAccess = FeatureAccess.Current;
        using var access = new FeatureAccessService(null, null, enabled: false);
        FeatureAccess.Current = access;
        try
        {
            var window = new MainWindow { Width = 640, Height = 480 };
            typeof(MainWindow).GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            var openedCount = 0;
            window.Opened += (_, _) => openedCount++;

            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, GetEventHandlerCount(access, "Changed"));

            window.Hide();
            Dispatcher.UIThread.RunJobs();
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(openedCount >= 2);
            Assert.Equal(1, GetEventHandlerCount(access, "Changed"));

            // Detaching on close still removes the handler from the subscribed instance even if Current changes.
            FeatureAccess.Current = null;
            await CloseWindowAndWaitAsync(window);
            Assert.Equal(0, GetEventHandlerCount(access, "Changed"));
        }
        finally
        {
            FeatureAccess.Current = previousAccess;
        }
    }

    [AvaloniaFact]
    [Trait("Kind", "UI")]
    public async Task ClosingMainWindowWhilePreviewIsOpenClearsPreviewContextAndDelegates()
    {
        var root = Directory.CreateTempSubdirectory("fk-preview-close-");
        using var theme = new FastListTestTheme();
        try
        {
            await using var fixture = await TabCacheFixture.CreateAsync(entries: 0,
                configureServices: services => services.AddSingleton<IFileService>(new FakeFileService(root.FullName)));
            var filePath = Path.Combine(root.FullName, "sample.txt");
            await File.WriteAllTextAsync(filePath, "preview lifetime test", TestContext.Current.CancellationToken);
            var entry = new FileSystemEntry
            {
                FullPath = filePath,
                Name = "sample.txt",
                Extension = ".txt"
            };

            await fixture.Window.OpenSuperPreviewForEntryAsync(entry);
            Dispatcher.UIThread.RunJobs();

            var preview = fixture.Window.FindControl<SuperPreviewView>("SuperPreviewControl")!;
            var fileListContextField = typeof(SuperPreviewView).GetField("_fileListContext", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.True(preview.IsVisible);
            Assert.NotNull(preview.PasswordPrompt);
            Assert.NotNull(preview.ExtractToNamedFolder);
            Assert.NotNull(fileListContextField.GetValue(preview));

            await CloseWindowAndWaitAsync(fixture.Window);

            Assert.False(preview.IsVisible);
            Assert.Null(preview.PasswordPrompt);
            Assert.Null(preview.ExtractToNamedFolder);
            Assert.Null(fileListContextField.GetValue(preview));
        }
        finally
        {
            if (root.Exists) root.Delete(true);
        }
    }

    [AvaloniaFact]
    [Trait("Kind", "UI")]
    public void MacWindowChromeCleansTrackedVibrancyWhenClosedWithoutPlatformHandle()
    {
        var baseline = MacWindowChrome.TrackedVibrancyCount;
        var window = new AppWindow { Width = 320, Height = 240 };
        var fakeNsView = new IntPtr(0x123456);
        var fakeEffectView = new IntPtr(0x654321);

        MacWindowChrome.TrackVisualEffectView(window, fakeNsView, fakeEffectView);
        Assert.Equal(baseline + 1, MacWindowChrome.TrackedVibrancyCount);

        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.Close();
        Dispatcher.UIThread.RunJobs();

        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(baseline, MacWindowChrome.TrackedVibrancyCount);
            Assert.False(MacWindowChrome.TryUntrackVisualEffectView(window, IntPtr.Zero, out _));
        }
        else
        {
            Assert.True(MacWindowChrome.TryUntrackVisualEffectView(window, IntPtr.Zero, out var removed));
            Assert.Equal(fakeEffectView, removed);
            Assert.Equal(baseline, MacWindowChrome.TrackedVibrancyCount);
        }
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public async Task LegacyRetryCompletesFailsOrRetiresOriginalTaskWithoutLeavingRunningRow()
    {
        var manager = new BackgroundTaskManager();

        // 1. Legacy retry whose action returns without updating the task transitions to Completed.
        var autoComplete = manager.AddTask("legacy-complete", BackgroundTaskKind.Generic,
            retryAction: () => Task.CompletedTask);
        manager.FailTask(autoComplete.Id, "initial error");
        manager.RetryTask(autoComplete.Id);
        await manager.WaitForCompletionAsync(autoComplete.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BackgroundTaskState.Completed, autoComplete.State);
        Assert.False(autoComplete.CanRetry);
        Assert.Null(autoComplete.RetryAction);

        // 2. Legacy retry whose action throws transitions back to Failed with retry expiry metadata.
        var throwing = manager.AddTask("legacy-throw", BackgroundTaskKind.Generic,
            retryAction: () => Task.FromException(new IOException("offline")));
        manager.FailTask(throwing.Id, "initial error");
        manager.RetryTask(throwing.Id);
        await manager.WaitForCompletionAsync(throwing.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BackgroundTaskState.Failed, throwing.State);
        Assert.True(throwing.CanRetry);
        Assert.NotNull(throwing.FailedAtUtc);
        manager.TrimFailedTasks(DateTime.UtcNow.Add(BackgroundTaskManager.FailedRetryLifetime).AddSeconds(1));
        Assert.False(throwing.CanRetry);
        Assert.Null(throwing.RetryAction);

        // 3. Legacy retry that spawns a successor task retires the original task row.
        BackgroundTaskInfo? successor = null;
        var replaced = manager.AddTask("legacy-successor", BackgroundTaskKind.RemoteUpload,
            retryAction: async () =>
            {
                await Task.Yield();
                successor = manager.AddTask("legacy-successor", BackgroundTaskKind.RemoteUpload);
                manager.CompleteTask(successor.Id);
            });
        manager.FailTask(replaced.Id, "disconnected");
        manager.RetryTask(replaced.Id);
        await manager.WaitForCompletionAsync(replaced.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(successor);
        Assert.Equal(BackgroundTaskState.Completed, successor!.State);
        Assert.True(replaced.IsTerminal);
        Assert.False(replaced.CanRetry);
        Assert.Null(replaced.RetryAction);
        Assert.DoesNotContain(replaced, manager.Tasks);
        Assert.Contains(successor, manager.Tasks);
        Assert.DoesNotContain(manager.Tasks, t => t.State == BackgroundTaskState.Running);
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public async Task FileTagServiceRetryReusesAndCompletesOriginalTaskRow()
    {
        var root = Directory.CreateTempSubdirectory("fk-tag-retry-");
        try
        {
            var filePath = Path.Combine(root.FullName, "tagged.txt");
            await File.WriteAllTextAsync(filePath, "content", TestContext.Current.CancellationToken);
            var store = new ToggleableTagStore { FailWrites = true };
            var manager = new BackgroundTaskManager();
            var factory = new DatabaseConnectionFactory(Path.Combine(root.FullName, "tags.db"));
            using var service = new FileTagService(factory, store: store, tasks: manager);
            var tag = await service.CreateTagAsync("Work", TestContext.Current.CancellationToken);

            var initial = await service.SetTagAsync([filePath], tag, true, TestContext.Current.CancellationToken);
            Assert.Equal(1, initial.PendingFiles);
            var task = Assert.Single(manager.Tasks);
            Assert.Equal(BackgroundTaskState.Failed, task.State);
            Assert.True(task.CanRetry);

            // First retry while still failing keeps the same task row in Failed (not Running orphan).
            manager.RetryTask(task.Id);
            await manager.WaitForCompletionAsync(task.Id).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(task, Assert.Single(manager.Tasks));
            Assert.Equal(BackgroundTaskState.Failed, task.State);
            Assert.True(task.CanRetry);
            Assert.NotNull(task.FailedAtUtc);

            // Second retry after store recovers drives the same task row to Completed and clears RetryAction.
            store.FailWrites = false;
            manager.RetryTask(task.Id);
            await manager.WaitForCompletionAsync(task.Id).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(task, Assert.Single(manager.Tasks));
            Assert.Equal(BackgroundTaskState.Completed, task.State);
            Assert.False(task.CanRetry);
            Assert.Null(task.RetryAction);
            Assert.Contains(new NativeFileTag("Work"), store.Files[filePath]);
        }
        finally
        {
            if (root.Exists) root.Delete(true);
        }
    }
}
