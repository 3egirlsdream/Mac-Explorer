using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Services.Subscriptions;

/// <summary>StoreKit owner for purchase UI. Feature permissions belong to FeatureAccessService.</summary>
internal static class SubscriptionAccess
{
    internal static SubscriptionService? Current => DistributionChannel.IsAppStore
        ? App.Services?.GetService<SubscriptionService>() : null;
}
