using System.Globalization;
using System.Runtime.CompilerServices;

namespace MacExplorer.Tests;

internal static class TestCulture
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Existing text assertions use Chinese. Keep native helper invocations and
        // worker threads deterministic without changing the OS language preference.
        var culture = CultureInfo.GetCultureInfo("zh-CN");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
