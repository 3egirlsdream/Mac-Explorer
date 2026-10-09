using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using MacExplorer.Services.Subscriptions;

namespace MacExplorer.Platforms.MacOS;

internal sealed class StoreKitBridge : ISubscriptionStore
{
    private const string Library = "libMacExplorerStoreKit.dylib";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Callback(long request, IntPtr json);
    private static readonly Callback Completion = OnCompletion;
    private static readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> Pending = new();
    private static long _next;
    private static StoreKitBridge? _active;
    private bool _started;
    public event Action? TransactionsChanged;

    public void Start()
    {
        if (_started || !DistributionChannel.IsAppStore) return;
        // Missing configuration becomes a verification error, never free access.
        if (string.IsNullOrWhiteSpace(SubscriptionService.ProductId)) return;
        _active = this;
        me_store_start(SubscriptionService.ProductId, Completion);
        _started = true;
    }

    private static void OnCompletion(long request, IntPtr json)
    {
        try
        {
            if (request == 0) { _active?.TransactionsChanged?.Invoke(); return; }
            var result = JsonSerializer.Deserialize<JsonElement>(Marshal.PtrToStringUTF8(json)!);
            if (!Pending.TryRemove(request, out var completion)) return;
            if (result.TryGetProperty("error", out var error)) completion.TrySetException(new InvalidOperationException(error.GetString()));
            else completion.TrySetResult(result);
        }
        catch (Exception ex)
        {
            if (Pending.TryRemove(request, out var completion)) completion.TrySetException(ex);
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async Task<JsonElement> RequestAsync(string operation, CancellationToken token)
    {
        if (!_started) throw new InvalidOperationException("StoreKit 未配置或尚未启动。");
        token.ThrowIfCancellationRequested();
        var id = Interlocked.Increment(ref _next);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending[id] = completion;
        try
        {
            me_store_request(id, operation);
            return await completion.Task.WaitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            if (Pending.TryRemove(id, out _)) me_store_cancel(id);
        }
    }

    public async Task<SubscriptionSnapshot> CheckAsync(CancellationToken token)
    {
        var result = await RequestAsync("check", token);
        var state = result.GetProperty("state").GetString() switch
        {
            "trial" => SubscriptionState.Trial, "subscribed" => SubscriptionState.Subscribed,
            "unsubscribed" => SubscriptionState.Unsubscribed,
            _ => throw new InvalidOperationException("无效的订阅状态。")
        };
        DateTimeOffset? expiry = result.TryGetProperty("expiresAt", out var value)
            ? DateTimeOffset.FromUnixTimeMilliseconds(value.GetInt64()) : null;
        return new(state, expiry);
    }

    public async Task<SubscriptionProduct> LoadProductAsync(CancellationToken token)
    {
        var result = await RequestAsync("product", token);
        return new(result.GetProperty("price").GetString()!, result.GetProperty("trialEligible").GetBoolean(), result.GetProperty("trialDays").GetInt32());
    }
    public async Task<PurchaseOutcome> PurchaseAsync(CancellationToken token)
    {
        var result = await RequestAsync("purchase", token);
        return result.GetProperty("outcome").GetString() switch
        {
            "purchased" => PurchaseOutcome.Purchased, "cancelled" => PurchaseOutcome.Cancelled,
            "pending" => PurchaseOutcome.Pending, _ => throw new InvalidOperationException("无效的购买结果。")
        };
    }
    public async Task RestoreAsync(CancellationToken token) => _ = await RequestAsync("restore", token);
    public async Task ManageAsync(CancellationToken token) => _ = await RequestAsync("manage", token);
    public void Dispose()
    {
        if (!_started) return;
        me_store_stop(); _started = false;
        if (ReferenceEquals(_active, this)) _active = null;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void me_store_start([MarshalAs(UnmanagedType.LPUTF8Str)] string productId, Callback callback);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void me_store_request(long id, [MarshalAs(UnmanagedType.LPUTF8Str)] string operation);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void me_store_cancel(long id);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void me_store_stop();
}
