using System.Globalization;

namespace MacExplorer.Services.Impl;

internal static partial class LocalizationCatalog
{
    internal sealed class LanguagePack(string id, string nativeName, string culture, string? fallback,
        Func<IReadOnlyDictionary<string, string>> strings, string[]? aliases = null)
    {
        public LanguageOption Option { get; } = new(id, nativeName);
        public string CultureName { get; } = culture;
        public string? Fallback { get; } = fallback;
        public IReadOnlyList<string> Aliases { get; } = aliases ?? [];
        private readonly Lazy<CultureInfo> _culture = new(() => CultureInfo.GetCultureInfo(culture));
        private readonly Lazy<IReadOnlyDictionary<string, string>> _strings = new(strings);
        public CultureInfo Culture => _culture.Value;
        public IReadOnlyDictionary<string, string> Strings => _strings.Value;
    }

    private static readonly IReadOnlyDictionary<string, LanguagePack> ById =
        CatalogData.Packs.ToDictionary(pack => pack.Option.Id, StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<string, LanguagePack> ByTag = CatalogData.Packs
        .SelectMany(pack => new[] { pack.Option.Id, pack.CultureName }.Concat(pack.Aliases)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(tag => (Tag: tag, Pack: pack)))
        .ToDictionary(entry => entry.Tag, entry => entry.Pack, StringComparer.OrdinalIgnoreCase);
    internal static IReadOnlyList<LanguageOption> Languages { get; } =
        Array.AsReadOnly(CatalogData.Packs.Select(pack => pack.Option).ToArray());
    internal static IReadOnlyDictionary<string, string> LiteralKeys => Literals.Keys;

    internal static LanguagePack GetPack(string id) => ById[id];

    internal static string? FindLanguage(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        if (ByTag.TryGetValue(tag, out var exact)) return exact.Option.Id;
        var parentTag = tag;
        while (parentTag.LastIndexOf('-') is var separator && separator >= 0)
        {
            parentTag = parentTag[..separator];
            if (ByTag.TryGetValue(parentTag, out var parent)) return parent.Option.Id;
        }
        // Keep the default for an unspecified Chinese script.
        if (parentTag.Equals("zh", StringComparison.OrdinalIgnoreCase)) return AppLanguage.ChineseSimplified;
        return null;
    }

    internal static LanguagePack ForCulture(CultureInfo culture) =>
        GetPack(FindLanguage(culture.Name) ?? AppLanguage.ChineseSimplified);

    internal static string Get(string key, LanguagePack pack)
    {
        if (key == "app.name") return AppBranding.GetName(pack.Culture);
        if (key == "app.copyright") return "© 2024–2026 " + AppBranding.GetName(pack.Culture);
        for (var current = pack; current is not null;
             current = current.Fallback is { } fallback ? GetPack(fallback) : null)
            if (current.Strings.TryGetValue(key, out var value)) return value;
        return key;
    }

    public static string Get(string key, CultureInfo culture) => Get(key, ForCulture(culture));
}
