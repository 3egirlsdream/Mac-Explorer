using MacExplorer.Services.Impl;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Subscriptions;

[Flags]
public enum PremiumFeature
{
    None = 0, SplitView = 1, BatchRename = 2, GlobalSearch = 4, ContentSearch = 8,
    AiClassification = 16, Copilot = 32, Network = 64, FileDelivery = 128, BackgroundThemes = 256,
    All = 511
}

public enum FeatureAccessState { Initializing, Free, AppTrial, Subscribed, StorageError, Website }
public enum TrialNotice { Welcome = 1, LastDay = 2, Expired = 4, StorageError = 8 }
public sealed record FeatureAccessSnapshot(FeatureAccessState State, DateTimeOffset? ExpiresAt = null);

public sealed class FeatureAccessDeniedException(PremiumFeature feature)
    : InvalidOperationException(LocalizationText.Get("access.required", FeatureAccess.FeatureName(feature)))
{
    public PremiumFeature Feature { get; } = feature;
}

public sealed class FeatureAccessService : IDisposable
{
    private readonly SubscriptionService? _subscription;
    private readonly IAppTrialStore? _store;
    private readonly TimeProvider _time;
    private readonly ILogger<FeatureAccessService>? _logger;
    private readonly object _gate = new();
    private readonly bool _enabled;
    private AppTrialRecord? _trial;
    private int _features;
    private bool _started, _disposed, _storageErrorNotified, _persistenceFailed;
    private Task? _start;
    private ITimer? _timer;
    private FeatureAccessSnapshot _snapshot;
    internal SubscriptionService? Subscription => _subscription;
    public FeatureAccessSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public event Action? Changed;

    public FeatureAccessService(SubscriptionService? subscription, IAppTrialStore? store, bool enabled,
        TimeProvider? time = null, ILogger<FeatureAccessService>? logger = null)
    {
        _subscription = subscription; _store = store; _enabled = enabled;
        _time = time ?? TimeProvider.System; _logger = logger;
        _features = enabled ? 0 : (int)PremiumFeature.All;
        _snapshot = new(enabled ? FeatureAccessState.Initializing : FeatureAccessState.Website);
    }

    public bool CanUse(PremiumFeature feature) => (Volatile.Read(ref _features) & (int)feature) == (int)feature;

    public Task StartAsync()
    {
        lock (_gate)
        {
            if (_disposed || !_enabled) return Task.CompletedTask;
            return _start ??= StartCoreAsync(_time.GetUtcNow());
        }
    }

    private async Task StartCoreAsync(DateTimeOffset readyAt)
    {
        AppTrialRecord? record = null;
        try { record = await Task.Run(() => _store!.LoadOrCreate(readyAt)).ConfigureAwait(false); }
        catch (Exception ex) { _logger?.LogError(ex, "Application trial could not be loaded or persisted"); }
        lock (_gate)
        {
            if (_disposed) return;
            _trial = record; _started = true;
            if (_subscription != null) _subscription.Changed += Refresh;
            _timer = _time.CreateTimer(_ => Refresh(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        Refresh();
    }

    public void Refresh()
    {
        lock (_gate)
        {
            if (_disposed || !_started) return;
            var now = _time.GetUtcNow();
            var subscribed = _subscription?.Snapshot.HasAccess(now) == true;
            if (_trial is { EligibilityConfirmed: false } trial
                && _subscription?.Snapshot.State != SubscriptionState.Checking)
            {
                // Only the first entitlement check decides initial eligibility. A later purchase never restarts or pauses the trial.
                var consumed = trial with { SubscriberSeen = subscribed, EligibilityConfirmed = true };
                try { _store!.Save(consumed); _trial = consumed; _persistenceFailed = false; }
                catch (Exception ex)
                {
                    _persistenceFailed = true;
                    _logger?.LogError(ex, "Could not persist application trial eligibility");
                }
            }
            var trialActive = !_persistenceFailed && _trial is { SubscriberSeen: false } && _trial.ExpiresAt > now;
            var state = subscribed ? FeatureAccessState.Subscribed : trialActive ? FeatureAccessState.AppTrial
                : _trial == null || _persistenceFailed ? FeatureAccessState.StorageError : FeatureAccessState.Free;
            var expiry = subscribed ? _subscription!.Snapshot.ExpiresAt : trialActive ? _trial!.ExpiresAt : null;
            Volatile.Write(ref _features, subscribed || trialActive ? (int)PremiumFeature.All : 0);
            Volatile.Write(ref _snapshot, new(state, expiry));
            DateTimeOffset? next = subscribed ? null : expiry;
            if (trialActive && (_trial!.Notices & (int)TrialNotice.LastDay) == 0 && _trial.ExpiresAt.AddDays(-1) > now)
                next = _trial.ExpiresAt.AddDays(-1);
            _timer?.Change(next is { } deadline ? TimeSpan.FromMilliseconds(Math.Max(1, (deadline - now).TotalMilliseconds))
                : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        Changed?.Invoke();
    }

    public TrialNotice? TakeNotice()
    {
        lock (_gate)
        {
            if (_disposed || !_started) return null;
            if (_trial == null || _persistenceFailed)
            {
                if (_storageErrorNotified) return null;
                _storageErrorNotified = true; return TrialNotice.StorageError;
            }
            if (Snapshot.State == FeatureAccessState.Subscribed || _trial.SubscriberSeen) return null;
            var now = _time.GetUtcNow();
            var notice = now >= _trial.ExpiresAt ? TrialNotice.Expired
                : now >= _trial.ExpiresAt.AddDays(-1) ? TrialNotice.LastDay : TrialNotice.Welcome;
            if ((_trial.Notices & (int)notice) != 0) return null;
            var record = _trial with { Notices = _trial.Notices | (int)notice };
            try { _store!.Save(record); _trial = record; return notice; }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Could not persist trial notification");
                if (_storageErrorNotified) return null;
                _storageErrorNotified = true; return TrialNotice.StorageError;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_subscription != null) _subscription.Changed -= Refresh;
            _timer?.Dispose(); Changed = null;
        }
    }
}
