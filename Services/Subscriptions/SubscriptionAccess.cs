using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Services.Subscriptions;

/// <summary>Shared gate for UI and non-UI command entry points; never cancels work already in progress.</summary>
internal static class SubscriptionAccess
{
    internal static SubscriptionService? Current => DistributionChannel.IsAppStore
        ? App.Services?.GetService<SubscriptionService>() : null;
    internal static bool IsLocked => Current?.IsLocked == true;
    internal static void RequireAccess() => Current?.RequireAccess();
}
