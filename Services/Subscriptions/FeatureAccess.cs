using MacExplorer.Models;
using MacExplorer.Services.Impl;

namespace MacExplorer.Services.Subscriptions;

/// <summary>Cached process service. Permission reads never resolve DI or read settings.</summary>
internal static class FeatureAccess
{
    private static readonly AsyncLocal<OperationGrant?> AcceptedFeatures = new();
    private sealed class OperationGrant(PremiumFeature features, OperationGrant? previous) : IDisposable
    {
        private int _active = 1;
        internal bool Allows(PremiumFeature feature) => Volatile.Read(ref _active) != 0 && (features & feature) == feature;
        public void Dispose()
        {
            Volatile.Write(ref _active, 0);
            AcceptedFeatures.Value = previous;
        }
    }
    private static IDisposable Grant(PremiumFeature features)
    {
        var grant = new OperationGrant(features, AcceptedFeatures.Value);
        AcceptedFeatures.Value = grant;
        return grant;
    }
    internal static IDisposable BeginOperation(PremiumFeature feature)
    {
        Require(feature);
        return Grant(feature);
    }
    internal static FeatureAccessService? Current { get; set; }
    internal static event Action<PremiumFeature>? UpgradeRequested;
    internal static bool CanUse(PremiumFeature feature) => feature == PremiumFeature.None || (Current?.CanUse(feature) ?? !DistributionChannel.IsAppStore);
    internal static bool TryUse(PremiumFeature feature)
    {
        if (CanUse(feature)) return true;
        if (Current?.Snapshot.State == FeatureAccessState.Initializing) return false;
        UpgradeRequested?.Invoke(feature); return false;
    }
    internal static void Require(PremiumFeature feature)
    {
        if (feature == PremiumFeature.None) return;
        if (AcceptedFeatures.Value?.Allows(feature) == true || FileTaskContext.Current?.HasFeatureGrant(feature) == true || CanUse(feature)) return;
        throw new FeatureAccessDeniedException(feature);
    }
    internal static async Task<T> RunUndoAsync<T>(PremiumFeature recordedFeatures, Func<Task<T>> action)
    {
        using var grant = Grant(recordedFeatures);
        return await action();
    }
    internal static async Task<T> RunAcceptedAsync<T>(PremiumFeature feature, Func<Task<T>> action)
    {
        using var grant = BeginOperation(feature);
        return await action();
    }
    internal static PremiumFeature ForPath(string path)
    {
        if (VirtualPath.IsRemotePath(path) || path == VirtualPath.Network) return PremiumFeature.Network;
        if (!AiPathHelper.IsAiPath(path)) return PremiumFeature.None;
        return AiPathHelper.Parse(path).Mode == AiViewMode.TextSearch
            ? PremiumFeature.ContentSearch : PremiumFeature.AiClassification;
    }
    internal static PremiumFeature ForRequest(FileTaskRequest request)
    {
        var feature = request.RequiredFeatures;
        if (request.Kind == BackgroundTaskKind.BatchRename) feature |= PremiumFeature.BatchRename;
        if (request.Kind is BackgroundTaskKind.RemoteUpload or BackgroundTaskKind.RemoteDownload
            || request.Sources.Any(VirtualPath.IsRemotePath)
            || request.Destination is { } destination && VirtualPath.IsRemotePath(destination)) feature |= PremiumFeature.Network;
        return feature;
    }
    internal static string FeatureName(PremiumFeature feature) => LocalizationText.Get("access.feature."
        + (feature != PremiumFeature.None && Enum.IsDefined(feature) ? feature : PremiumFeature.All));
}
