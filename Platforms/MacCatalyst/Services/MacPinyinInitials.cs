using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using MacExplorer.Services.Search;

namespace MacExplorer.Platforms.MacCatalyst.Services;

/// <summary>
/// CoreFoundation's Han-Latin transliteration; no Windows code pages or third-party
/// dictionaries. This is per-character initials, not word-level polyphonic resolution.
/// </summary>
public sealed class MacPinyinInitials : IPinyinInitials
{
    private readonly ConcurrentDictionary<int, string> _initials = new();

    public string GetInitials(string name)
    {
        if (!OperatingSystem.IsMacOS()) return string.Empty;
        var result = new StringBuilder(name.Length);
        var containsHan = false;
        foreach (var rune in name.EnumerateRunes())
        {
            if (IsHan(rune.Value))
            {
                containsHan = true;
                result.Append(_initials.GetOrAdd(rune.Value, TransliterateInitial));
            }
            else result.Append(rune.ToString());
        }
        return containsHan ? SearchQuery.Fold(result.ToString()) : string.Empty;
    }

    private static bool IsHan(int value) => value is >= 0x3400 and <= 0x9FFF
        or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF;

    private static string TransliterateInitial(int scalar)
    {
        var original = new Rune(scalar).ToString();
        var source = IntPtr.Zero;
        var mutable = IntPtr.Zero;
        var transform = IntPtr.Zero;
        try
        {
            source = CFStringCreateWithCString(IntPtr.Zero, original, Utf8);
            transform = CFStringCreateWithCString(IntPtr.Zero, "Han-Latin", Utf8);
            if (source == IntPtr.Zero || transform == IntPtr.Zero) return original;
            mutable = CFStringCreateMutableCopy(IntPtr.Zero, 0, source);
            if (mutable == IntPtr.Zero || !CFStringTransform(mutable, IntPtr.Zero, transform, false)) return original;
            var capacity = checked((int)CFStringGetMaximumSizeForEncoding(CFStringGetLength(mutable), Utf8) + 1);
            var buffer = new byte[capacity];
            if (!CFStringGetCString(mutable, buffer, buffer.Length, Utf8)) return original;
            var length = Array.IndexOf(buffer, (byte)0);
            var latin = Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length)
                .Normalize(NormalizationForm.FormD).ToUpperInvariant();
            foreach (var c in latin)
                if (c is >= 'A' and <= 'Z') return c.ToString();
            return original;
        }
        finally
        {
            if (mutable != IntPtr.Zero) CFRelease(mutable);
            if (source != IntPtr.Zero) CFRelease(source);
            if (transform != IntPtr.Zero) CFRelease(transform);
        }
    }

    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100;
    [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateMutableCopy(IntPtr allocator, nint maxLength, IntPtr source);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CFStringTransform(IntPtr text, IntPtr range, IntPtr transform, [MarshalAs(UnmanagedType.U1)] bool reverse);
    [DllImport(CoreFoundation)] private static extern nint CFStringGetLength(IntPtr text);
    [DllImport(CoreFoundation)] private static extern nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CFStringGetCString(IntPtr text, [Out] byte[] buffer, nint capacity, uint encoding);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr value);
}
