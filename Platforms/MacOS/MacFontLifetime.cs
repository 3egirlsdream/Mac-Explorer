using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
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
    private readonly Func<string, FontCollectionKey, GlyphTypeface?, bool> _cacheGlyph;
    private delegate bool FindGlyph(string family, FontCollectionKey key, bool nearest, [NotNullWhen(true)] out GlyphTypeface? glyph);
    private readonly FindGlyph _findGlyph;
    private readonly ConcurrentDictionary<FontCollectionKey, GlyphTypeface?> _systemUiFonts = new();
    private readonly Func<FontStyle, FontWeight, FontStretch, GlyphTypeface?>? _createSystemUiGlyph;

    internal MacFontLifetime(FontCollectionBase inner, FontManager? manager = null)
    {
        _inner = inner;
        _cacheGlyph = typeof(FontCollectionBase).GetMethod("TryAddGlyphTypeface", BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(string), typeof(FontCollectionKey), typeof(GlyphTypeface)])!
            .CreateDelegate<Func<string, FontCollectionKey, GlyphTypeface?, bool>>(inner);
        _findGlyph = typeof(FontCollectionBase).GetMethod("TryGetGlyphTypeface", BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(string), typeof(FontCollectionKey), typeof(bool), typeof(GlyphTypeface).MakeByRefType()])!
            .CreateDelegate<FindGlyph>(inner);
        _createSystemUiGlyph = BuildSystemUiFactory(manager);
    }

    private static Func<FontStyle, FontWeight, FontStretch, GlyphTypeface?>? BuildSystemUiFactory(FontManager? manager)
    {
        if (manager == null) return null;
        var platformImpl = typeof(FontManager).GetProperty("PlatformImpl", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(manager);
        var tryCreate = platformImpl?.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m => m.Name == "TryCreateGlyphTypeface"
                && m.GetParameters() is { Length: 5 } p
                && p[0].ParameterType == typeof(string));
        var glyphCtor = typeof(GlyphTypeface).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(IPlatformTypeface), typeof(FontSimulations)]);
        if (platformImpl == null || tryCreate == null || glyphCtor == null) return null;

        return (style, weight, stretch) =>
        {
            object?[] invokeArgs = [AppleSystemUiFont, style, weight, stretch, null];
            if (tryCreate.Invoke(platformImpl, invokeArgs) is true && invokeArgs[4] is IPlatformTypeface platformTypeface)
                return (GlyphTypeface)glyphCtor.Invoke([platformTypeface, platformTypeface.FontSimulations]);
            return null;
        };
    }

    internal static void Prepare()
    {
        if (!OperatingSystem.IsMacOS() || typeof(FontManager).Assembly.GetName().Version != new Version(12, 0, 4, 0)) return;
        var manager = FontManager.Current;
        var system = manager.SystemFonts;
        if (system is not FontCollectionBase source || system.GetType().FullName != "Avalonia.Media.Fonts.SystemFontCollection") return;
        // AddFontCollection disposes its previous collection. This decorator takes
        // ownership of that live collection instead; do not dispose active fonts.
        if (typeof(FontManager).GetField("_fontCollections", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(manager)
            is ConcurrentDictionary<Uri, IFontCollection> collections)
            collections.TryUpdate(system.Key, new MacFontLifetime(source, manager), system);
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
        if (_createSystemUiGlyph != null && IsSystemUiFamily(familyName))
        {
            var key = new FontCollectionKey(style, weight, stretch);
            glyphTypeface = _systemUiFonts.GetOrAdd(key, k => _createSystemUiGlyph(k.Style, k.Weight, k.Stretch));
            if (glyphTypeface != null) return true;
        }

        lock (_sync)
        {
            var success = _inner.TryGetGlyphTypeface(familyName, style, weight, stretch, out glyphTypeface);
            if (success) _cacheGlyph(familyName, new FontCollectionKey(style, weight, stretch), glyphTypeface);
            return success;
        }
    }

    public bool TryCreateSyntheticGlyphTypeface(GlyphTypeface glyphTypeface, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? syntheticGlyphTypeface)
    {
        if (_createSystemUiGlyph != null && IsSystemUiFamily(glyphTypeface.FamilyName))
        {
            var key = new FontCollectionKey(style, weight, stretch);
            syntheticGlyphTypeface = _systemUiFonts.GetOrAdd(key, k => _createSystemUiGlyph(k.Style, k.Weight, k.Stretch));
            if (syntheticGlyphTypeface != null) return true;
        }

        lock (_sync)
        {
            var key = new FontCollectionKey(style, weight, stretch);
            if (_findGlyph(glyphTypeface.FamilyName, key, false, out syntheticGlyphTypeface)) return true;
            var success = _inner.TryCreateSyntheticGlyphTypeface(glyphTypeface, style, weight, stretch, out syntheticGlyphTypeface);
            if (success) _cacheGlyph(glyphTypeface.FamilyName, key, syntheticGlyphTypeface);
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
