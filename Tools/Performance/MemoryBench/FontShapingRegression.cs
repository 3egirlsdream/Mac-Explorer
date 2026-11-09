using System.Globalization;
using System.Reflection;
using Avalonia.Harfbuzz;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using MacExplorer.TextShaping;

internal static class FontShapingRegression
{
    private static bool _verified;
    private static readonly FieldInfo ShaperTypeface = typeof(GlyphTypeface)
        .GetField("_textShaperTypeface", BindingFlags.NonPublic | BindingFlags.Instance)!;

    internal static void Verify()
    {
        if (_verified) return;
        var comparisons = 0;
        foreach (var family in new[] { "PingFang SC", "Helvetica Neue", "Menlo", "Arial" })
        foreach (var weight in new[] { FontWeight.Normal, FontWeight.SemiBold, FontWeight.Bold })
        {
            if (!FontManager.Current.TryGetGlyphTypeface(new Typeface(family, weight: weight), out var source))
                throw new InvalidOperationException("A native shaping regression font was unavailable.");
            // Separate glyph instances prevent either shaper's cached native face
            // from being used by the other; the cached platform font stays owned
            // by FontManager, so only these test-created shaping faces are disposed.
            var before = new GlyphTypeface(source.PlatformTypeface, source.FontSimulations);
            var after = new GlyphTypeface(source.PlatformTypeface, source.FontSimulations);
            var original = new HarfBuzzTextShaper();
            var patched = new PohHarfBuzzTextShaper();
            using var originalFace = original.CreateTypeface(before);
            using var patchedFace = patched.CreateTypeface(after);
            ShaperTypeface.SetValue(before, originalFace);
            ShaperTypeface.SetValue(after, patchedFace);
            foreach (var (text, culture, bidi) in new (string, string, sbyte)[]
            {
                ("文件列表与设置", "zh-CN", 0), ("檔案列表與設定", "zh-TW", 0),
                ("Office affinity AV fi ffi", "en-US", 0), ("Élève, cœur et français", "fr-FR", 0),
                ("العربية", "ar-SA", 1), ("עברית", "he-IL", 1),
                ("Cmd ⌘⇧⌥ 中文 e\u0301 😀", "en-US", 0),
                ("a\tb\r\n", "en-US", 0), ("a\r", "en-US", 0), ("", "en-US", 0)
            })
            foreach (var features in new[] { (FontFeatureCollection?)null, FontFeatureCollection.Parse("liga=0,kern=0") })
            {
                // Slices exercise clusters relative to surrounding text as well.
                var containing = "prefix " + text + " suffix";
                var memory = containing.AsMemory(7, text.Length);
                var expected = original.ShapeText(memory, new TextShaperOptions(before, 13, bidi,
                    CultureInfo.GetCultureInfo(culture), 24, 0.25, features));
                var actual = patched.ShapeText(memory, new TextShaperOptions(after, 13, bidi,
                    CultureInfo.GetCultureInfo(culture), 24, 0.25, features));
                if (expected.Length != actual.Length || expected.BidiLevel != actual.BidiLevel)
                    throw new InvalidOperationException("The font-table allocator changed shaping output.");
                for (var i = 0; i < expected.Length; i++)
                    if (expected[i] != actual[i])
                        throw new InvalidOperationException("The font-table allocator changed a glyph or its position.");
                comparisons++;
            }
        }
        MacExplorer.Services.MemoryDiagnostics.Record("fonts.shaping_regression", new { comparisons, passed = true });
        _verified = true;
    }
}
