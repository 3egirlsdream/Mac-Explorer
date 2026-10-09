using System.IO;
using Lang.Avalonia;
using Lang.Avalonia.Json;

namespace MacExplorer.Services.Markdown;

/// <summary>The renderer prints its raw resource keys when no language plugin is registered.</summary>
internal static class MarkdownLocalization
{
    public static void Register()
    {
        // Next to the executable in build output, under Contents/Resources in the .app bundle.
        var resourceFolder = Path.Combine(AppContext.BaseDirectory, "I18n");
        if (!Directory.Exists(resourceFolder))
            resourceFolder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "I18n"));
        I18nManager.Instance.Register(
            new JsonLangPlugin { ResourceFolder = resourceFolder },
            Services.Impl.LocalizationService.ResolveSystemLanguage() == Services.AppLanguage.English
                ? System.Globalization.CultureInfo.GetCultureInfo("en-US")
                : System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), out _);
    }
}
