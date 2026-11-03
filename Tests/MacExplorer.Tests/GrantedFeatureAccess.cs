using MacExplorer.Services.Subscriptions;

namespace MacExplorer.Tests;

// Functional regressions assume access has already been granted; entitlement tests own their access state.
internal sealed class GrantedFeatureAccess : IDisposable
{
    private readonly FeatureAccessService? _previous = FeatureAccess.Current;
    private readonly FeatureAccessService _access = new(null, null, false);

    public GrantedFeatureAccess() => FeatureAccess.Current = _access;

    public void Dispose()
    {
        FeatureAccess.Current = _previous;
        _access.Dispose();
    }
}
