using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;

namespace MacExplorer.Platforms.MacOS;

// Avalonia 12.0.4 can repeatedly synthesize the same system font through
// OpenStream/FromStream. CoreText retains each materialized font in its registry.
// Its synthetic path omits FamilyName when the alternate names are empty.
// Complete that entry in the existing cache, preserving all font/fallback choices.
// Also cache macOS native system UI font (.AppleSystemUIFont / SF Pro) weights under
// "System Font" so Medium/SemiBold/Bold resolve directly without slow fallback lookups
// or synthetic emboldening.
internal sealed class MacFontLifetime : IFontCollection
{
    private const string SystemFontAlias = "System Font";
    private const string AppleSystemUiFont = ".AppleSystemUIFont";

    private readonly object _sync = new();
    private readonly FontCollectionBase _inner;
    private readonly ConcurrentDictionary<FontCollectionKey, GlyphTypeface?> _systemUiFonts = new();
    private readonly IFontManagerImpl? _platform;

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "TryAddGlyphTypeface")]
    private static extern bool CacheGlyph(FontCollectionBase collection, string family, FontCollectionKey key, GlyphTypeface? glyph);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "TryGetGlyphTypeface")]
    private static extern bool FindGlyph(FontCollectionBase collection, string family, FontCollectionKey key,
        bool allowNearestMatch, [NotNullWhen(true)] out GlyphTypeface? glyph);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_PlatformImpl")]
    private static extern IFontManagerImpl GetPlatform(FontManager manager);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_fontCollections")]
    private static extern ref readonly ConcurrentDictionary<Uri, IFontCollection> GetCollections(FontManager manager);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "TryCreateGlyphTypeface")]
    private static extern bool TryCreatePlatformGlyph(IFontManagerImpl platform, string family, FontStyle style,
        FontWeight weight, FontStretch stretch, [NotNullWhen(true)] out IPlatformTypeface? glyph);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern GlyphTypeface CreateGlyph(IPlatformTypeface platformTypeface, FontSimulations simulations);

    internal MacFontLifetime(FontCollectionBase inner, FontManager? manager = null)
    {
        _inner = inner;
        _platform = manager is null ? null : GetPlatform(manager);
    }

    private GlyphTypeface? GetSystemUiGlyph(FontCollectionKey key, IFontManagerImpl platform) =>
        _systemUiFonts.GetOrAdd(key, static (k, fontManager) =>
        {
            if (TryCreatePlatformGlyph(fontManager, AppleSystemUiFont, k.Style, k.Weight, k.Stretch, out var typeface))
                return CreateGlyph(typeface, typeface.FontSimulations);
            return null;
        }, platform);

    internal static void Prepare()
    {
        if (!OperatingSystem.IsMacOS() || typeof(FontManager).Assembly.GetName().Version != new Version(12, 0, 4, 0)) return;
        var manager = FontManager.Current;
        var system = manager.SystemFonts;
        if (system is not FontCollectionBase source || system.GetType().FullName != "Avalonia.Media.Fonts.SystemFontCollection") return;
        // AddFontCollection disposes its previous collection. This decorator takes
        // ownership of that live collection instead; do not dispose active fonts.
        GetCollections(manager).TryUpdate(system.Key, new MacFontLifetime(source, manager), system);
    }

    public Uri Key => _inner.Key;
    public int Count => _inner.Count;
    public FontFamily this[int index] => _inner[index];
    public IEnumerator<FontFamily> GetEnumerator() => _inner.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static bool IsSystemUiFamily(string familyName)
        => string.Equals(familyName, SystemFontAlias, StringComparison.OrdinalIgnoreCase)
           || string.Equals(familyName, AppleSystemUiFont, StringComparison.OrdinalIgnoreCase);

    public bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
    {
        if (_platform != null && IsSystemUiFamily(familyName))
        {
            var key = new FontCollectionKey(style, weight, stretch);
            glyphTypeface = GetSystemUiGlyph(key, _platform);
            if (glyphTypeface != null) return true;
        }

        lock (_sync)
        {
            var success = _inner.TryGetGlyphTypeface(familyName, style, weight, stretch, out glyphTypeface);
            if (success) CacheGlyph(_inner, familyName, new FontCollectionKey(style, weight, stretch), glyphTypeface);
            return success;
        }
    }

    public bool TryCreateSyntheticGlyphTypeface(GlyphTypeface glyphTypeface, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? syntheticGlyphTypeface)
    {
        if (_platform != null && IsSystemUiFamily(glyphTypeface.FamilyName))
        {
            var key = new FontCollectionKey(style, weight, stretch);
            syntheticGlyphTypeface = GetSystemUiGlyph(key, _platform);
            if (syntheticGlyphTypeface != null) return true;
        }

        lock (_sync)
        {
            var key = new FontCollectionKey(style, weight, stretch);
            if (FindGlyph(_inner, glyphTypeface.FamilyName, key, false, out syntheticGlyphTypeface)) return true;
            var success = _inner.TryCreateSyntheticGlyphTypeface(glyphTypeface, style, weight, stretch, out syntheticGlyphTypeface);
            if (success) CacheGlyph(_inner, glyphTypeface.FamilyName, key, syntheticGlyphTypeface);
            return success;
        }
    }

    public bool TryMatchCharacter(int codepoint, FontStyle fontStyle, FontWeight fontWeight, FontStretch fontStretch,
        string? familyName, CultureInfo? culture, out Typeface typeface)
        => _inner.TryMatchCharacter(codepoint, fontStyle, fontWeight, fontStretch, familyName, culture, out typeface);

    public bool TryGetFamilyTypefaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces)
        => _inner.TryGetFamilyTypefaces(familyName, out familyTypefaces);

    public bool TryGetNearestMatch(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
        => _inner.TryGetNearestMatch(familyName, style, weight, stretch, out glyphTypeface);

    public void Dispose() => ((IDisposable)_inner).Dispose();
}
