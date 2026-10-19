using System.Globalization;

namespace MacExplorer.Services.Impl;

internal static class LocalizationText
{
    public static string Get(string chinese, params object?[] arguments)
    {
        var service = LocalizationService.Current;
        var culture = service?.Culture ?? CultureInfo.GetCultureInfo("zh-CN");
        var key = LocalizationCatalog.LiteralKeys.GetValueOrDefault(chinese, chinese);
        var text = service is not null ? service[key] : LocalizationCatalog.Get(key, culture);
        return arguments.Length == 0 ? text : string.Format(culture, text, arguments);
    }
}
