using MacExplorer.Services.Subscriptions;
using Xunit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Views;

namespace MacExplorer.Tests;

public sealed class SubscriptionTests
{
    private sealed class Store : ISubscriptionStore
    {
        public event Action? TransactionsChanged;
        public int Starts, Checks, Products, Purchases, Restores, Manages;
        public bool Disposed;
        public Func<CancellationToken, Task<SubscriptionSnapshot>> Read = _ => Task.FromResult(new SubscriptionSnapshot(SubscriptionState.Unsubscribed));
        public Func<CancellationToken, Task<SubscriptionProduct>> ReadProduct = _ => Task.FromResult(new SubscriptionProduct("¥9.90", true, 7));
        public PurchaseOutcome Outcome = PurchaseOutcome.Purchased;
        public void Start() => Starts++;
        public Task<SubscriptionSnapshot> CheckAsync(CancellationToken token) { Interlocked.Increment(ref Checks); return Read(token); }
        public Task<SubscriptionProduct> LoadProductAsync(CancellationToken token) { Products++; return ReadProduct(token); }
        public Task<PurchaseOutcome> PurchaseAsync(CancellationToken token) { Purchases++; return Task.FromResult(Outcome); }
        public Task RestoreAsync(CancellationToken token) { Restores++; return Task.CompletedTask; }
        public Task ManageAsync(CancellationToken token) { Manages++; return Task.CompletedTask; }
        public void Emit() => TransactionsChanged?.Invoke();
        public void Dispose() => Disposed = true;
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
        private readonly List<Timer> _timers = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed)
        {
            _now += elapsed;
            foreach (var timer in _timers.ToArray()) timer.FireIfDue();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            timer.Change(dueTime, period); _timers.Add(timer); return timer;
        }
        private sealed class Timer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset _due = DateTimeOffset.MaxValue;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                _due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.GetUtcNow() + dueTime;
                return true;
            }
            public void FireIfDue()
            {
                if (_disposed || _due > clock.GetUtcNow()) return;
                _due = DateTimeOffset.MaxValue; callback(state);
            }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    [Fact]
    public async Task ConstructionAndPreWindowActivationDoNoStoreWorkAndInitialCheckAllowsUse()
    {
        var pending = new TaskCompletionSource<SubscriptionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Store { Read = _ => pending.Task };
        using var service = new SubscriptionService(store, true);
        await service.RefreshAsync();
        Assert.Equal(0, store.Starts); Assert.Equal(0, store.Checks); Assert.Equal(0, store.Products);
        service.Start(); service.Start();
        Assert.False(service.IsLocked); Assert.Equal(1, store.Starts);
        pending.SetResult(new(SubscriptionState.Unsubscribed));
        await service.RefreshAsync();
        Assert.True(service.IsLocked);
        Assert.Throws<InvalidOperationException>(service.RequireAccess);
        Assert.Equal(0, store.Restores);
    }

    [Theory]
    [InlineData(SubscriptionState.Trial)]
    [InlineData(SubscriptionState.Subscribed)]
    public async Task ActiveEntitlementSurvivesNetworkFailureOnlyUntilVerifiedExpiry(SubscriptionState state)
    {
        var clock = new Clock();
        var store = new Store { Read = _ => Task.FromResult(new SubscriptionSnapshot(state, clock.GetUtcNow().AddDays(1))) };
        using var service = new SubscriptionService(store, true, clock);
        service.Start(); await service.RefreshAsync(); Assert.False(service.IsLocked);
        store.Read = _ => throw new IOException("offline");
        await service.RefreshAsync(); Assert.False(service.IsLocked); Assert.Equal(state, service.Snapshot.State);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.True(service.IsLocked);
        await service.RefreshAsync(); Assert.Equal(SubscriptionState.VerificationFailed, service.Snapshot.State);
        Assert.Equal(0, store.Restores);
    }

    [Fact]
    public async Task TimeoutLocksUnknownUserAndIgnoresLateSuccess()
    {
        var pending = new TaskCompletionSource<SubscriptionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Store { Read = _ => pending.Task };
        using var service = new SubscriptionService(store, true, timeout: TimeSpan.FromMilliseconds(30));
        service.Start(); await service.RefreshAsync();
        Assert.True(service.IsLocked); Assert.Equal(SubscriptionState.VerificationFailed, service.Snapshot.State);
        pending.SetResult(new(SubscriptionState.Subscribed, DateTimeOffset.UtcNow.AddYears(1)));
        Assert.True(service.IsLocked);
    }

    [Fact]
    public async Task EmptyVerifiedResultRevokesOldAccessRatherThanKeepingCache()
    {
        var store = new Store { Read = _ => Task.FromResult(new SubscriptionSnapshot(SubscriptionState.Subscribed, DateTimeOffset.UtcNow.AddYears(1))) };
        using var service = new SubscriptionService(store, true);
        service.Start(); await service.RefreshAsync(); Assert.False(service.IsLocked);
        store.Read = _ => Task.FromResult(new SubscriptionSnapshot(SubscriptionState.Unsubscribed));
        await service.RefreshAsync(); Assert.True(service.IsLocked);
        Assert.Equal(SubscriptionState.Unsubscribed, service.Snapshot.State);
    }

    [Fact]
    public async Task ExpiryTimerLocksBeforeRenewalCheckCompletesAndVerifiedRenewalUnlocks()
    {
        var clock = new Clock();
        var store = new Store { Read = _ => Task.FromResult(new SubscriptionSnapshot(SubscriptionState.Trial, clock.GetUtcNow().AddMinutes(2))) };
        using var service = new SubscriptionService(store, true, clock);
        service.Start(); await service.RefreshAsync();
        var pending = new TaskCompletionSource<SubscriptionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Read = _ => pending.Task;
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(service.IsLocked);
        pending.SetResult(new(SubscriptionState.Subscribed, clock.GetUtcNow().AddYears(1)));
        await service.RefreshAsync(); Assert.False(service.IsLocked);
    }

    [Fact]
    public async Task PeriodicCheckRunsAfterFifteenMinutesAndStopsAfterDisposal()
    {
        var clock = new Clock(); var store = new Store();
        var service = new SubscriptionService(store, true, clock);
        service.Start(); await service.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(14)); Assert.Equal(1, store.Checks);
        clock.Advance(TimeSpan.FromMinutes(1)); await service.RefreshAsync(); Assert.Equal(2, store.Checks);
        service.Dispose(); clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(2, store.Checks); Assert.True(store.Disposed);
    }

    [Fact]
    public async Task ConcurrentChecksAreMerged()
    {
        var pending = new TaskCompletionSource<SubscriptionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Store { Read = _ => pending.Task };
        using var service = new SubscriptionService(store, true);
        service.Start();
        var first = service.RefreshAsync(); var second = service.RefreshAsync();
        Assert.Same(first, second);
        pending.SetResult(new(SubscriptionState.Unsubscribed));
        await first; Assert.Equal(1, store.Checks);
    }

    [Fact]
    public async Task ProductFailureCannotRevokeValidAccess()
    {
        var store = new Store
        {
            Read = _ => Task.FromResult(new SubscriptionSnapshot(SubscriptionState.Trial, DateTimeOffset.UtcNow.AddDays(7))),
            ReadProduct = _ => throw new IOException("product lookup failed")
        };
        using var service = new SubscriptionService(store, true);
        service.Start(); await Task.WhenAll(service.RefreshAsync(), service.LoadProductAsync());
        Assert.False(service.IsLocked); Assert.NotNull(service.ProductError);
    }

    [Theory]
    [InlineData(PurchaseOutcome.Cancelled)]
    [InlineData(PurchaseOutcome.Pending)]
    public async Task PendingAndCancelledPurchasesNeverUnlock(PurchaseOutcome outcome)
    {
        var store = new Store { Outcome = outcome };
        using var service = new SubscriptionService(store, true);
        service.Start(); await service.RefreshAsync(); await service.PurchaseAsync();
        Assert.True(service.IsLocked); Assert.NotNull(service.ActionMessage); Assert.False(service.IsBusy);
    }

    [Fact]
    public async Task SuccessfulPurchaseAndExplicitRestoreRefreshEntitlements()
    {
        var store = new Store();
        using var service = new SubscriptionService(store, true);
        service.Start(); await service.RefreshAsync();
        store.Read = _ => Task.FromResult(new SubscriptionSnapshot(SubscriptionState.Subscribed, DateTimeOffset.UtcNow.AddYears(1)));
        await service.PurchaseAsync(); Assert.False(service.IsLocked); Assert.Equal(0, store.Restores);
        await service.RestoreAsync(); Assert.Equal(1, store.Restores);
        await service.ManageAsync(); Assert.Equal(1, store.Manages);
    }

    [Fact]
    public async Task TransactionArrivingDuringCheckForcesNewCheck()
    {
        var pending = new TaskCompletionSource<SubscriptionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Store { Read = _ => pending.Task };
        using var service = new SubscriptionService(store, true);
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += () => { if (service.Snapshot.State == SubscriptionState.Subscribed) updated.TrySetResult(); };
        service.Start(); var first = service.RefreshAsync();
        store.Emit();
        store.Read = _ => Task.FromResult(new SubscriptionSnapshot(SubscriptionState.Subscribed, DateTimeOffset.UtcNow.AddYears(1)));
        pending.SetResult(new(SubscriptionState.Unsubscribed));
        await first;
        await updated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(service.IsLocked);
    }

    [Fact]
    public async Task WebsiteNeverStartsStoreOrLocks()
    {
        var store = new Store();
        using var service = new SubscriptionService(store, false);
        service.Start(); await service.RefreshAsync(); await service.LoadProductAsync(); await service.RestoreAsync();
        service.RequireAccess(); Assert.False(service.IsLocked);
        Assert.Equal(0, store.Starts + store.Checks + store.Products + store.Restores);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedLockDisablesEveryWindowAndUnlockRestoresContent(bool dark)
    {
        var store = new Store();
        using var service = new SubscriptionService(store, true);
        var firstButton = new Button { Content = "文件操作" };
        var secondButton = new Button { Content = "速递操作" };
        var first = new AppWindow(service) { Width = 640, Height = 480, Content = firstButton,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        var second = new AppWindow(service) { Width = 360, Height = 400, Content = secondButton,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            first.Show(); second.Show();
            Assert.True(firstButton.IsEffectivelyEnabled);
            service.Start(); await Task.WhenAll(service.RefreshAsync(), service.LoadProductAsync());
            Dispatcher.UIThread.RunJobs();
            Assert.False(firstButton.IsEffectivelyEnabled); Assert.False(secondButton.IsEffectivelyEnabled);
            foreach (var window in new[] { first, second })
            {
                var view = Assert.Single(window.GetVisualDescendants().OfType<SubscriptionView>());
                Assert.True(view.IsEffectivelyVisible);
                var host = Assert.Single(window.GetVisualDescendants().OfType<Grid>().Where(g => g.Name == "SubscriptionHost"));
                Assert.Equal(2, Grid.GetRowSpan(host));
                Assert.Equal(0, view.TranslatePoint(default, window)!.Value.Y);
                var subscribe = view.FindControl<Button>("SubscribeButton")!;
                var position = subscribe.TranslatePoint(default, window)!.Value;
                Assert.InRange(position.Y + subscribe.Bounds.Height, 1, window.ClientSize.Height);
                Assert.NotNull(view.FindControl<Image>("WorkflowImage")!.Source);
                Assert.Contains(view.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "恢复购买") && b.IsEffectivelyEnabled);
                Assert.Contains(view.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "免费试用 7 天") && b.IsEffectivelyEnabled);
            }
            store.Read = _ => Task.FromResult(new SubscriptionSnapshot(SubscriptionState.Subscribed, DateTimeOffset.UtcNow.AddYears(1)));
            await service.RefreshAsync(); Dispatcher.UIThread.RunJobs();
            Assert.True(firstButton.IsEffectivelyEnabled); Assert.True(secondButton.IsEffectivelyEnabled);
        }
        finally { first.Close(); second.Close(); }
    }
}
