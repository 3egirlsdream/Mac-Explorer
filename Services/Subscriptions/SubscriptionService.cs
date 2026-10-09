using System.Reflection;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Subscriptions;

public enum SubscriptionState { Checking, Trial, Subscribed, Unsubscribed, VerificationFailed }

public sealed record SubscriptionSnapshot(SubscriptionState State, DateTimeOffset? ExpiresAt = null)
{
    public bool HasAccess(DateTimeOffset now) => State is SubscriptionState.Trial or SubscriptionState.Subscribed
        && ExpiresAt > now;
}

public sealed record SubscriptionProduct(string Price, bool TrialEligible, int TrialDays);
public enum PurchaseOutcome { Purchased, Cancelled, Pending }

public interface ISubscriptionStore : IDisposable
{
    event Action? TransactionsChanged;
    void Start();
    Task<SubscriptionSnapshot> CheckAsync(CancellationToken token);
    Task<SubscriptionProduct> LoadProductAsync(CancellationToken token);
    Task<PurchaseOutcome> PurchaseAsync(CancellationToken token);
    Task RestoreAsync(CancellationToken token);
    Task ManageAsync(CancellationToken token);
}

/// <summary>One process-wide entitlement owner. No StoreKit work occurs in the constructor.</summary>
public sealed class SubscriptionService : IDisposable
{
    public static string ProductId => typeof(SubscriptionService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "AppStoreSubscriptionProductId")?.Value ?? "";
    private readonly ISubscriptionStore _store;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;
    private readonly ILogger<SubscriptionService>? _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private Task? _check;
    private Task? _productLoad;
    private ITimer? _timer;
    private bool _started, _disposed, _initialCheck = true;
    private int _action;
    public bool Enabled { get; }
    public SubscriptionSnapshot Snapshot { get; private set; } = new(SubscriptionState.Checking);
    public SubscriptionProduct? Product { get; private set; }
    public string? ProductError { get; private set; }
    public string? ActionMessage { get; private set; }
    public bool IsBusy => Volatile.Read(ref _action) != 0;
    public bool IsLocked => Enabled && !_initialCheck && !Snapshot.HasAccess(_time.GetUtcNow());
    public event Action? Changed;

    public SubscriptionService(ISubscriptionStore store, bool enabled, TimeProvider? time = null, TimeSpan? timeout = null, ILogger<SubscriptionService>? logger = null)
    {
        _store = store; Enabled = enabled; _time = time ?? TimeProvider.System;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
        _logger = logger;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (!Enabled || _started || _disposed) return;
            _started = true;
            _store.TransactionsChanged += OnTransactionsChanged;
            try { _store.Start(); }
            catch (Exception ex) { _logger?.LogDebug(ex, "StoreKit initialization failed"); }
            _timer = _time.CreateTimer(_ => { Changed?.Invoke(); _ = RefreshAsync(); }, null, TimeSpan.FromMinutes(15), Timeout.InfiniteTimeSpan);
        }
        _ = RefreshAsync();
        _ = LoadProductAsync();
    }

    private void OnTransactionsChanged() => _ = RefreshAfterCurrentAsync();

    private async Task RefreshAfterCurrentAsync()
    {
        Task? current;
        lock (_gate) current = _check is { IsCompleted: false } ? _check : null;
        if (current != null) await current.ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
    }

    public Task RefreshAsync()
    {
        lock (_gate)
        {
            if (!Enabled || !_started || _disposed) return Task.CompletedTask;
            return _check is { IsCompleted: false } ? _check : _check = Task.Run(CheckCoreAsync);
        }
    }

    private async Task CheckCoreAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(_timeout);
        try
        {
            var result = await _store.CheckAsync(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            if (_disposed) return;
            // A successful empty entitlement result clears old access, including refunds.
            Snapshot = result;
        }
        catch (Exception ex) when (!_lifetime.IsCancellationRequested)
        {
            _logger?.LogDebug(ex, "StoreKit entitlement check failed");
            if (!Snapshot.HasAccess(_time.GetUtcNow()))
                Snapshot = new(SubscriptionState.VerificationFailed);
        }
        catch (OperationCanceledException) { return; }
        finally
        {
            if (!_disposed)
            {
                _initialCheck = false;
                ScheduleNextCheck();
                Changed?.Invoke();
            }
        }
    }

    private void ScheduleNextCheck()
    {
        var delay = TimeSpan.FromMinutes(15);
        if (Snapshot.ExpiresAt is { } expiry && Snapshot.HasAccess(_time.GetUtcNow()))
            delay = TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(delay.TotalMilliseconds, (expiry - _time.GetUtcNow()).TotalMilliseconds)));
        _timer?.Change(delay, Timeout.InfiniteTimeSpan);
    }

    public Task LoadProductAsync()
    {
        lock (_gate)
        {
            if (!Enabled || _disposed) return Task.CompletedTask;
            return _productLoad is { IsCompleted: false } ? _productLoad : _productLoad = Task.Run(async () =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(_timeout);
                try { Product = await _store.LoadProductAsync(timeout.Token).WaitAsync(timeout.Token); ProductError = null; }
                catch (Exception ex) when (!_lifetime.IsCancellationRequested)
                {
                    _logger?.LogDebug(ex, "StoreKit product lookup failed");
                    ProductError = "暂时无法加载订阅价格，请重试。";
                }
                catch (OperationCanceledException) { }
                if (!_disposed) Changed?.Invoke();
            });
        }
    }

    public Task PurchaseAsync() => RunActionAsync(async token =>
    {
        var result = await _store.PurchaseAsync(token);
        ActionMessage = result switch { PurchaseOutcome.Pending => "购买等待批准，批准后将自动解锁。", PurchaseOutcome.Cancelled => "已取消购买。", _ => null };
        if (result == PurchaseOutcome.Purchased) { await RefreshAfterCurrentAsync(); await LoadProductAsync(); }
    });
    public Task RestoreAsync() => RunActionAsync(async token => { await _store.RestoreAsync(token); await RefreshAfterCurrentAsync(); await LoadProductAsync(); });
    public Task ManageAsync() => RunActionAsync(token => _store.ManageAsync(token));

    private async Task RunActionAsync(Func<CancellationToken, Task> action)
    {
        if (!Enabled || _disposed || Interlocked.CompareExchange(ref _action, 1, 0) != 0) return;
        ActionMessage = null; Changed?.Invoke();
        try { await action(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { ActionMessage = "操作未完成，请检查 App Store 账户或网络后重试。"; }
        finally { Interlocked.Exchange(ref _action, 0); if (!_disposed) Changed?.Invoke(); }
    }

    public void RequireAccess()
    {
        if (IsLocked) throw new InvalidOperationException("订阅未生效，请先订阅或恢复购买。");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _timer?.Dispose(); _lifetime.Cancel();
            _store.TransactionsChanged -= OnTransactionsChanged;
            _store.Dispose();
        }
    }
}
