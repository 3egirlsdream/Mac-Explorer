using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Avalonia;
using Lang.Avalonia;
using MacExplorer.Services;

namespace MacExplorer.Services.Impl;

public sealed class LocalizationService : ILocalizationService, IDisposable
{
    public const string SettingKey = "app_language";
    private static readonly string SystemLanguage = DetectSystemLanguage();
    private readonly ISettingsService _settings;
    private readonly Func<string> _systemLanguage;
    private string _language;
    private LocalizationCatalog.LanguagePack _pack;
    private readonly LocalizationService? _previous = Current;
    private readonly CultureInfo _previousCulture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _previousUiCulture = CultureInfo.CurrentUICulture;
    private readonly CultureInfo? _previousDefaultCulture = CultureInfo.DefaultThreadCurrentCulture;
    private readonly CultureInfo? _previousDefaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

    public static LocalizationService? Current { get; private set; }
    public event Action? LanguageChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public LocalizationService(ISettingsService settings, Func<string>? systemLanguage = null)
    {
        _settings = settings;
        _systemLanguage = systemLanguage ?? ResolveSystemLanguage;
        _language = Parse(settings.Get(SettingKey));
        _pack = LocalizationCatalog.GetPack(EffectiveLanguage);
        Current = this;
        ApplyCulture();
    }

    public string Language => _language;
    public IReadOnlyList<LanguageOption> AvailableLanguages => LocalizationCatalog.Languages;
    public CultureInfo Culture => _pack.Culture;
    public string EffectiveLanguage => _language == AppLanguage.System
        ? MapSystemLanguage(_systemLanguage()) : _language;

    public string this[string key] => LocalizationCatalog.Get(key, _pack);

    public string Get(string key, params object?[] arguments)
    {
        var value = this[key];
        return arguments.Length == 0 ? value : string.Format(Culture, value, arguments);
    }

    public void SetLanguage(string language)
    {
        language = string.Equals(language, AppLanguage.System, StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.System : LocalizationCatalog.FindLanguage(language)
                ?? throw new ArgumentOutOfRangeException(nameof(language));
        if (Application.Current is not null && !Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Invoke(() => SetLanguage(language));
            return;
        }
        if (_language == language) return;
        _language = language;
        _settings.Set(SettingKey, language);
        _pack = LocalizationCatalog.GetPack(EffectiveLanguage);
        ApplyCulture();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        LanguageChanged?.Invoke();
    }

    public void Dispose()
    {
        if (!ReferenceEquals(Current, this)) return;
        Current = _previous;
        CultureInfo.CurrentCulture = _previousCulture;
        CultureInfo.CurrentUICulture = _previousUiCulture;
        CultureInfo.DefaultThreadCurrentCulture = _previousDefaultCulture;
        CultureInfo.DefaultThreadCurrentUICulture = _previousDefaultUiCulture;
        try { I18nManager.Instance.Culture = _previousUiCulture; }
        catch { }
    }

    private void ApplyCulture()
    {
        var culture = Culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        try { I18nManager.Instance.Culture = culture; }
        catch { /* Markdown localization may not be registered during design-time construction. */ }
    }

    private static string Parse(string? value) =>
        string.Equals(value, AppLanguage.System, StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.System : LocalizationCatalog.FindLanguage(value) ?? AppLanguage.System;

    public static string ResolveSystemLanguage() => SystemLanguage;

    public static string MapSystemLanguage(string? preferred) =>
        LocalizationCatalog.FindLanguage(preferred) ?? AppLanguage.ChineseSimplified;

    private static string DetectSystemLanguage()
    {
        // Read the OS preference before applying the application's culture. CurrentUICulture
        // can already be overridden and does not reliably reflect AppleLanguages.
        if (!OperatingSystem.IsMacOS()) return MapSystemLanguage(CultureInfo.InstalledUICulture.Name);
        var languages = CFLocaleCopyPreferredLanguages();
        if (languages == IntPtr.Zero) return AppLanguage.ChineseSimplified;
        try
        {
            if (CFArrayGetCount(languages) == 0) return AppLanguage.ChineseSimplified;
            var first = CFArrayGetValueAtIndex(languages, 0);
            var length = CFStringGetLength(first);
            var buffer = new char[(int)length];
            CFStringGetCharacters(first, new CFRange(0, length), buffer);
            return MapSystemLanguage(new string(buffer));
        }
        finally { CFRelease(languages); }
    }

    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CFRange(nint location, nint length)
    {
        public readonly nint Location = location;
        public readonly nint Length = length;
    }
    [DllImport(CoreFoundation)] private static extern IntPtr CFLocaleCopyPreferredLanguages();
    [DllImport(CoreFoundation)] private static extern nint CFArrayGetCount(IntPtr array);
    [DllImport(CoreFoundation)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
    [DllImport(CoreFoundation)] private static extern nint CFStringGetLength(IntPtr value);
    [DllImport(CoreFoundation)] private static extern void CFStringGetCharacters(IntPtr value, CFRange range, [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2)] char[] buffer);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr value);

}

public static class LocalizationSystem
{
    public static string Resolve() => LocalizationService.ResolveSystemLanguage();
}
