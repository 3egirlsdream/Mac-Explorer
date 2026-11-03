using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Subscriptions;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Area", "Subscription")]
public sealed class FeatureAccessTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        private readonly List<ClockTimer> _timers = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(this, callback, state); _timers.Add(timer);
            timer.Change(dueTime, period); return timer;
        }
        public void Advance(TimeSpan time)
        {
            Now += time;
            foreach (var timer in _timers.ToArray()) timer.Fire();
        }
        private sealed class ClockTimer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset _due = DateTimeOffset.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { _due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.Now + dueTime; return true; }
            public void Fire()
            { if (clock.Now < _due) return; _due = DateTimeOffset.MaxValue; callback(state); }
            public void Dispose() => _due = DateTimeOffset.MaxValue;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class TrialStore : IAppTrialStore
    {
        public AppTrialRecord? Record;
        public int Reads, Writes;
        public bool Fail;
        public AppTrialRecord LoadOrCreate(DateTimeOffset readyAt)
        {
            Reads++;
            if (Fail) throw new IOException("disk unavailable");
            return Record ??= new(readyAt, readyAt.AddHours(168));
        }
        public void Save(AppTrialRecord record)
        {
            Writes++;
            if (Fail) throw new IOException("disk unavailable");
            Record = record;
        }
    }

    private sealed class AppleStore : ISubscriptionStore
    {
        public event Action? TransactionsChanged { add { } remove { } }
        public SubscriptionSnapshot Value = new(SubscriptionState.Unsubscribed);
        public bool Fail;
        public void Start() { }
        public Task<SubscriptionSnapshot> CheckAsync(CancellationToken token) => Fail
            ? Task.FromException<SubscriptionSnapshot>(new IOException("offline")) : Task.FromResult(Value);
        public Task<SubscriptionProduct> LoadProductAsync(CancellationToken token) => Task.FromException<SubscriptionProduct>(new IOException("no product"));
        public Task<PurchaseOutcome> PurchaseAsync(CancellationToken token) => Task.FromResult(PurchaseOutcome.Cancelled);
        public Task RestoreAsync(CancellationToken token) => Task.CompletedTask;
        public Task ManageAsync(CancellationToken token) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class Scope(FeatureAccessService access) : IDisposable
    {
        private readonly FeatureAccessService? _previous = FeatureAccess.Current;
        public void Apply() => FeatureAccess.Current = access;
        public void Dispose() => FeatureAccess.Current = _previous;
    }

    [Fact, Trait("Kind", "Unit")]
    public async Task TrialIsExactly168HoursAndRestartDoesNotResetIt()
    {
        var clock = new Clock(); var store = new TrialStore();
        using (var first = new FeatureAccessService(null, store, true, clock))
        {
            await first.StartAsync(); await first.StartAsync();
            Assert.Equal(1, store.Reads);
            Assert.Equal(clock.Now.AddHours(168), first.Snapshot.ExpiresAt);
            Assert.True(first.CanUse(PremiumFeature.All));
            Assert.Equal(TrialNotice.Welcome, first.TakeNotice());
            Assert.Null(first.TakeNotice());
        }
        clock.Now = clock.Now.AddHours(167);
        using var restarted = new FeatureAccessService(null, store, true, clock);
        await restarted.StartAsync();
        Assert.True(restarted.CanUse(PremiumFeature.All));
        Assert.Equal(TrialNotice.LastDay, restarted.TakeNotice());
        Assert.Null(restarted.TakeNotice());
        clock.Now = clock.Now.AddHours(1); restarted.Refresh();
        Assert.Equal(FeatureAccessState.Free, restarted.Snapshot.State);
        Assert.False(restarted.CanUse(PremiumFeature.All));
        Assert.True(restarted.CanUse(PremiumFeature.None));
        Assert.Equal(TrialNotice.Expired, restarted.TakeNotice());
        Assert.Null(restarted.TakeNotice());
    }

    [Fact, Trait("Kind", "Unit")]
    public async Task FirstInitializationIsTransactionalAndCorruptRecordsAreNotReset()
    {
        using var fixture = new FileDeliveryTests.Fixture();
        var factory = new DatabaseConnectionFactory(Path.Combine(fixture.Root, "test.db"));
        var now = new Clock().Now;
        var records = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            new AppTrialStore(factory).LoadOrCreate(now.AddHours(i)))));
        Assert.All(records, r => Assert.Equal(records[0], r));
        var store = new AppTrialStore(factory);
        store.Save(records[0] with { Notices = 3 });
        Assert.Equal(3, store.LoadOrCreate(now.AddDays(10)).Notices);
        using var connection = factory.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE app_settings SET value='invalid' WHERE key='app_trial_v1'";
        command.ExecuteNonQuery();
        Assert.Throws<System.Text.Json.JsonException>(() => store.LoadOrCreate(now));
    }

    [Fact, Trait("Kind", "Unit")]
    public async Task StorageFailuresLeaveFreeFeaturesUsableAndNeverClaimAnActiveTrial()
    {
        using var access = new FeatureAccessService(null, new TrialStore { Fail = true }, true);
        await access.StartAsync();
        Assert.Equal(FeatureAccessState.StorageError, access.Snapshot.State);
        Assert.False(access.CanUse(PremiumFeature.All));
        Assert.True(access.CanUse(PremiumFeature.None));
        Assert.Equal(TrialNotice.StorageError, access.TakeNotice());
        Assert.Null(access.TakeNotice());
    }

    [Fact, Trait("Kind", "Unit")]
    public async Task SharedTimerExpiresTheTrialAndOnlyOneWindowClaimsEachNotice()
    {
        var clock = new Clock(); using var access = new FeatureAccessService(null, new TrialStore(), true, clock);
        await access.StartAsync();
        var notices = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(access.TakeNotice)));
        Assert.Single(notices.Where(n => n == TrialNotice.Welcome));
        clock.Advance(TimeSpan.FromDays(6));
        Assert.Equal(TrialNotice.LastDay, access.TakeNotice());
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(FeatureAccessState.Free, access.Snapshot.State);
        Assert.False(access.CanUse(PremiumFeature.All));
        Assert.Equal(TrialNotice.Expired, access.TakeNotice());
    }

    [Fact, Trait("Kind", "Unit")]
    public async Task ASubscriptionPurchasedDuringTrialDoesNotRestartOrConsumeItsFixedDeadline()
    {
        var clock = new Clock(); var local = new TrialStore(); var apple = new AppleStore();
        using var subscription = new SubscriptionService(apple, true, clock);
        subscription.Start(); await subscription.RefreshAsync();
        using var access = new FeatureAccessService(subscription, local, true, clock);
        await access.StartAsync(); var originalExpiry = access.Snapshot.ExpiresAt;
        clock.Now = clock.Now.AddDays(1);
        apple.Value = new(SubscriptionState.Subscribed, clock.Now.AddYears(1)); await subscription.RefreshAsync();
        Assert.Equal(FeatureAccessState.Subscribed, access.Snapshot.State);
        apple.Value = new(SubscriptionState.Unsubscribed); await subscription.RefreshAsync();
        Assert.Equal(FeatureAccessState.AppTrial, access.Snapshot.State);
        Assert.Equal(originalExpiry, access.Snapshot.ExpiresAt);
        Assert.False(local.Record!.SubscriberSeen);
    }

    [Theory, InlineData(SubscriptionState.Subscribed), InlineData(SubscriptionState.Trial), Trait("Kind", "Unit")]
    public async Task ExistingAppleRightsDoNotAccumulateAnApplicationTrial(SubscriptionState state)
    {
        var clock = new Clock(); var local = new TrialStore();
        var apple = new AppleStore { Value = new(state, clock.Now.AddHours(2)) };
        using var subscription = new SubscriptionService(apple, true, clock);
        subscription.Start(); await subscription.RefreshAsync();
        using var access = new FeatureAccessService(subscription, local, true, clock);
        await access.StartAsync();
        Assert.Equal(FeatureAccessState.Subscribed, access.Snapshot.State);
        Assert.True(local.Record!.SubscriberSeen);
        Assert.Null(access.TakeNotice());
        apple.Fail = true; await subscription.RefreshAsync();
        Assert.True(access.CanUse(PremiumFeature.All));
        clock.Now = clock.Now.AddHours(3); access.Refresh();
        Assert.False(access.CanUse(PremiumFeature.All));
        using var restart = new FeatureAccessService(null, local, true, clock);
        await restart.StartAsync();
        Assert.Equal(FeatureAccessState.Free, restart.Snapshot.State);
    }

    [Fact, Trait("Kind", "Unit")]
    public async Task ProductAndVerificationFailuresDoNotBlockLocalTrialOrWebsite()
    {
        var apple = new AppleStore { Fail = true };
        using var subscription = new SubscriptionService(apple, true);
        subscription.Start(); await Task.WhenAll(subscription.RefreshAsync(), subscription.LoadProductAsync());
        using var access = new FeatureAccessService(subscription, new TrialStore(), true);
        await access.StartAsync();
        Assert.True(access.CanUse(PremiumFeature.All));
        using var website = new FeatureAccessService(null, new TrialStore { Fail = true }, false);
        await website.StartAsync();
        Assert.Equal(FeatureAccessState.Website, website.Snapshot.State);
        Assert.True(website.CanUse(PremiumFeature.All));
    }

    [Fact, Trait("Kind", "Unit")]
    public async Task AcceptedQueuedTaskSurvivesExpiryWithoutAuthorizingUnrelatedFeatures()
    {
        var clock = new Clock(); var store = new TrialStore();
        using var access = new FeatureAccessService(null, store, true, clock);
        await access.StartAsync();
        using var scope = new Scope(access); scope.Apply();
        var manager = new BackgroundTaskManager(); manager.MaxConcurrency = 1;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = manager.RunAsync(new(BackgroundTaskKind.Copy, "local", []), async _ =>
        { entered.SetResult(); await release.Task; return true; });
        await entered.Task;
        var queued = manager.RunAsync(new(BackgroundTaskKind.BatchRename, "batch", []), async context =>
        {
            FeatureAccess.Require(PremiumFeature.BatchRename);
            Assert.Throws<FeatureAccessDeniedException>(() => FeatureAccess.Require(PremiumFeature.Network));
            await context.CheckpointAsync(); return true;
        });
        var batch = manager.Tasks.Single(t => t.Kind == BackgroundTaskKind.BatchRename);
        manager.PauseTask(batch.Id);
        clock.Now = clock.Now.AddDays(8); access.Refresh();
        release.SetResult(); await first;
        Assert.Equal(BackgroundTaskState.Paused, batch.State);
        manager.ResumeTask(batch.Id);
        await queued;
        Assert.All(manager.Tasks, task => Assert.Equal(BackgroundTaskState.Completed, task.State));
        await Assert.ThrowsAsync<FeatureAccessDeniedException>(() => manager.RunAsync(
            new(BackgroundTaskKind.BatchRename, "new", []), _ => Task.FromResult(true)));
        Assert.True(await manager.RunAsync(new(BackgroundTaskKind.Copy, "still free", []), _ => Task.FromResult(true)));
    }

    [Fact, Trait("Kind", "Unit")]
    public async Task FreeTaskContextDoesNotBypassFeatureChecksAndRetriesNeedNewAccess()
    {
        var clock = new Clock();
        using var access = new FeatureAccessService(null, new TrialStore(), true, clock);
        await access.StartAsync(); using var scope = new Scope(access); scope.Apply();
        var manager = new BackgroundTaskManager();
        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => manager.RunAsync<bool>(new(BackgroundTaskKind.BatchRename, "retry", []), _ =>
        { calls++; throw new IOException("failed"); }));
        var task = Assert.Single(manager.Tasks);
        clock.Now = clock.Now.AddDays(8); access.Refresh();
        manager.RetryTask(task.Id);
        Assert.Equal(1, calls); Assert.Equal(BackgroundTaskState.NeedsAttention, task.State); Assert.NotNull(task.ErrorMessage);
        await manager.RunAsync(new(BackgroundTaskKind.Copy, "local", []), _ =>
        { Assert.Throws<FeatureAccessDeniedException>(() => FeatureAccess.Require(PremiumFeature.BatchRename)); return Task.FromResult(true); });
    }

    [Fact, Trait("Kind", "Unit")]
    public async Task CompletedOperationsCannotLeakTheirGrantToDelayedCallbacks()
    {
        var clock = new Clock(); using var access = new FeatureAccessService(null, new TrialStore(), true, clock);
        await access.StartAsync(); using var scope = new Scope(access); scope.Apply();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? callback = null;
        await FeatureAccess.RunAcceptedAsync(PremiumFeature.Network, () =>
        {
            callback = Task.Run(async () =>
            {
                await release.Task;
                Assert.Throws<FeatureAccessDeniedException>(() => FeatureAccess.Require(PremiumFeature.Network));
            });
            return Task.FromResult(true);
        });
        Task? taskCallback = null;
        var manager = new BackgroundTaskManager();
        await manager.RunAsync(new(BackgroundTaskKind.BatchRename, "completed", []), _ =>
        {
            taskCallback = Task.Run(async () =>
            {
                await release.Task;
                Assert.Throws<FeatureAccessDeniedException>(() => FeatureAccess.Require(PremiumFeature.BatchRename));
            });
            return Task.FromResult(true);
        });
        clock.Now = clock.Now.AddDays(8); access.Refresh();
        release.SetResult(); await Task.WhenAll(callback!, taskCallback!);
    }

    [Fact, Trait("Kind", "Unit")]
    public async Task DeliveryRejectsNewEntriesButKeepsAndCanClearStagingAfterExpiry()
    {
        using var fixture = new FileDeliveryTests.Fixture(); var clock = new Clock();
        using var access = new FeatureAccessService(null, new TrialStore(), true, clock);
        await access.StartAsync(); using var scope = new Scope(access); scope.Apply();
        using var delivery = new FileDeliveryService(fixture.Settings, fixture.Tags);
        var file = Path.Combine(fixture.Root, "keep.txt"); File.WriteAllText(file, "keep");
        delivery.StagePaths([file]);
        clock.Now = clock.Now.AddDays(8); access.Refresh();
        Assert.Throws<FeatureAccessDeniedException>(() => delivery.StagePaths([file]));
        Assert.Throws<FeatureAccessDeniedException>(() => delivery.AddFolder(fixture.Root));
        Assert.Single(delivery.StagedPaths); delivery.ClearStaging();
        Assert.Empty(delivery.StagedPaths); Assert.Equal("keep", File.ReadAllText(file));
    }

    [Fact, Trait("Kind", "Unit")]
    public async Task RecordedBatchCanBeUndoneAfterExpiryWithoutUnlockingNewBatchOperations()
    {
        using var fixture = new FileDeliveryTests.Fixture(); var clock = new Clock();
        using var access = new FeatureAccessService(null, new TrialStore(), true, clock);
        await access.StartAsync(); using var scope = new Scope(access); scope.Apply();
        var original = Path.Combine(fixture.Root, "original.txt");
        var renamed = Path.Combine(fixture.Root, "renamed.txt");
        await File.WriteAllTextAsync(original, "keep"); File.Move(original, renamed);
        var files = new MacExplorer.Platforms.MacCatalyst.Services.MacFileService();
        var history = new FileOperationHistoryService(files);
        await history.RecordBatchRenameAsync([new() { OriginalPath = original, NewPath = renamed,
            OriginalName = "original.txt", NewName = "renamed.txt" }]);
        clock.Now = clock.Now.AddDays(8); access.Refresh();
        var result = await history.UndoAsync(Assert.Single(history.Records).Id);
        Assert.True(result.Success, result.Message);
        Assert.Equal("keep", await File.ReadAllTextAsync(original));
        Assert.False(File.Exists(renamed));
        Assert.Throws<FeatureAccessDeniedException>(() => FeatureAccess.Require(PremiumFeature.BatchRename));
    }

    [Fact, Trait("Kind", "Unit")]
    public async Task PermissionQueriesAllocateNothingAndNeverReadStorage()
    {
        var store = new TrialStore(); using var access = new FeatureAccessService(null, store, true);
        await access.StartAsync();
        for (var i = 0; i < 10000; i++) access.CanUse(PremiumFeature.All);
        var reads = store.Reads; var writes = store.Writes;
        var before = GC.GetAllocatedBytesForCurrentThread(); var enabled = 0;
        for (var i = 0; i < 1000000; i++) if (access.CanUse(PremiumFeature.GlobalSearch)) enabled++;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(1000000, enabled); Assert.Equal(0, allocated);
        Assert.Equal(reads, store.Reads); Assert.Equal(writes, store.Writes);
    }

    [AvaloniaTheory, InlineData(false), InlineData(true), Trait("Kind", "UI"), Trait("Smoke", "UI")]
    public async Task BackgroundExpiryPreservesConfigurationAndPurchaseRestoresIt(bool dark)
    {
        using var fixture = new FileDeliveryTests.Fixture(); var clock = new Clock();
        var apple = new AppleStore(); using var subscription = new SubscriptionService(apple, true, clock);
        subscription.Start(); await subscription.RefreshAsync();
        using var access = new FeatureAccessService(subscription, new TrialStore(), true, clock);
        await access.StartAsync(); using var scope = new Scope(access); scope.Apply();
        using var theme = new BackgroundThemeService(fixture.Settings, fixture.Root, access);
        await theme.SelectPresetAsync("animal-panda"); theme.Update(true, 63);
        var options = theme.Options;
        Assert.True(theme.IsActive);
        clock.Now = clock.Now.AddDays(8); access.Refresh(); Dispatcher.UIThread.RunJobs();
        Assert.False(theme.IsActive); Assert.Equal(options, theme.Options); Assert.NotNull(theme.Image);
        await Assert.ThrowsAsync<FeatureAccessDeniedException>(() => theme.SelectPresetAsync("animal-rabbit"));
        apple.Value = new(SubscriptionState.Subscribed, clock.Now.AddYears(1));
        await subscription.RefreshAsync(); Dispatcher.UIThread.RunJobs();
        Assert.True(theme.IsActive); Assert.Equal(options, theme.Options);
        var window = new AppWindow(subscription) { Width = 380, Height = 480,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); Assert.Empty(window.GetVisualDescendants().OfType<SubscriptionView>());
            theme.Update(false, 63); apple.Value = new(SubscriptionState.Unsubscribed);
            await subscription.RefreshAsync();
            apple.Value = new(SubscriptionState.Subscribed, clock.Now.AddYears(1));
            await subscription.RefreshAsync(); Dispatcher.UIThread.RunJobs();
            Assert.False(theme.IsActive); Assert.False(theme.Options.Enabled);
        }
        finally { window.Close(); }
    }
}
