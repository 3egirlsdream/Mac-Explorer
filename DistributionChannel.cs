namespace MacExplorer;

/// <summary>The build channel is fixed at compile time, never by a user preference.</summary>
public static class DistributionChannel
{
#if MAC_APP_STORE
    public static bool IsAppStore => true;
#else
    public static bool IsAppStore => false;
#endif
    public static bool SupportsExternalPlugins => !IsAppStore;
    public static bool SupportsSystemIntegration => !IsAppStore;
    public static void RequireWebsite(string feature)
    {
        if (IsAppStore) throw new NotSupportedException($"App Store 版本不支持{feature}。");
    }
}
