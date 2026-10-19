using System.Globalization;
using MacExplorer.Services.Impl;

namespace MacExplorer;

public static class AppBranding
{
    public static string Name => GetName(LocalizationService.Current?.Culture ?? CultureInfo.GetCultureInfo("zh-CN"));

    internal static string GetName(CultureInfo culture) => DistributionChannel.IsAppStore
        ? LocalizationCatalog.Get("app.store_name", culture) : "Mac Explorer";
}
