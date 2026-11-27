using System.IO;
using Lang.Avalonia;
using Lang.Avalonia.Json;

namespace MacExplorer.Services.Markdown;

/// <summary>The renderer prints its raw resource keys when no language plugin is registered.</summary>
internal static class MarkdownLocalization
{
    public static void Register()
    {
        var resourceFolder = Path.Combine(RuntimePaths.BundleResourceDirectory, "I18n");
        I18nManager.Instance.Register(
            new JsonLangPlugin { ResourceFolder = resourceFolder },
            Services.Impl.LocalizationCatalog.GetPack(Services.Impl.LocalizationService.ResolveSystemLanguage()).Culture, out _);
    }
}
