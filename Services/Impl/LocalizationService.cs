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
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly AppLanguage SystemLanguage = DetectSystemLanguage();
    private readonly ISettingsService _settings;
    private readonly Func<AppLanguage> _systemLanguage;
    private AppLanguage _language;
    private readonly LocalizationService? _previous = Current;
    private readonly CultureInfo _previousCulture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _previousUiCulture = CultureInfo.CurrentUICulture;
    private readonly CultureInfo? _previousDefaultCulture = CultureInfo.DefaultThreadCurrentCulture;
    private readonly CultureInfo? _previousDefaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

    public static LocalizationService? Current { get; private set; }
    public event Action? LanguageChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public LocalizationService(ISettingsService settings, Func<AppLanguage>? systemLanguage = null)
    {
        _settings = settings;
        _systemLanguage = systemLanguage ?? ResolveSystemLanguage;
        Current = this;
        _language = Parse(settings.Get(SettingKey) ?? "system");
        ApplyCulture();
    }

    public AppLanguage Language => _language;
    public CultureInfo Culture => EffectiveLanguage == AppLanguage.English ? English : Chinese;
    public AppLanguage EffectiveLanguage => _language == AppLanguage.System ? _systemLanguage() : _language;

    public string this[string key] => LocalizationCatalog.Get(key, Culture);

    public string Get(string key, params object?[] arguments)
    {
        var value = this[key];
        return arguments.Length == 0 ? value : string.Format(Culture, value, arguments);
    }

    public void SetLanguage(AppLanguage language)
    {
        if (!Enum.IsDefined(language)) throw new ArgumentOutOfRangeException(nameof(language));
        if (Application.Current is not null && !Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Invoke(() => SetLanguage(language));
            return;
        }
        if (_language == language) return;
        _language = language;
        _settings.Set(SettingKey, language switch
        {
            AppLanguage.English => "en",
            AppLanguage.ChineseSimplified => "zh-CN",
            _ => "system"
        });
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

    private static AppLanguage Parse(string? value) => value?.ToLowerInvariant() switch
    {
        "en" or "en-us" => AppLanguage.English,
        "zh" or "zh-cn" => AppLanguage.ChineseSimplified,
        _ => AppLanguage.System
    };

    public static AppLanguage ResolveSystemLanguage()
        => SystemLanguage;

    public static AppLanguage MapSystemLanguage(string? preferred)
        => preferred?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true
            ? AppLanguage.English : AppLanguage.ChineseSimplified;

    private static AppLanguage DetectSystemLanguage()
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

internal static partial class LocalizationCatalog
{
    internal static readonly IReadOnlyDictionary<string, string> Zh = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["language.system"] = "跟随系统", ["language.zh"] = "简体中文", ["language.en"] = "English",
        ["language.label"] = "语言", ["language.section"] = "语言",
        ["settings.general"] = "通用", ["settings.general.description"] = "让 MacExplorer 更贴合你的日常使用习惯。",
        ["settings.system"] = "系统", ["settings.language"] = "应用语言",
        ["settings.default_manager"] = "设为默认文件管理器",
        ["settings.default_manager.description"] = "通过系统打开文件夹及兼容应用的「在 Finder 中显示」时使用 MacExplorer。更改后需重启电脑。",
        ["settings.copilot_enabled"] = "启用 Copilot", ["settings.copilot_enabled.description"] = "启用后显示 AI 助手按钮；关闭时收起助手面板。",
        ["settings.ai_analysis"] = "AI 智能分析", ["settings.ai_analysis.description"] = "在本机分析图片内容与 PDF 文字。照片地点联网解析需单独许可。",
        ["settings.privacy"] = "隐私", ["settings.photo_location"] = "照片地点联网解析",
        ["settings.photo_location.description"] = "将 GPS 经纬度发送给 Apple，转换为地点名称。默认关闭，可随时撤回。",
        ["settings.file_access"] = "文件访问", ["settings.choose_folder"] = "选择文件夹…",
        ["settings.retry_access"] = "重试授权恢复", ["settings.file_delivery"] = "文件速递",
        ["settings.file_delivery.title"] = "在菜单栏显示文件速递",
        ["settings.file_delivery.description"] = "从常用文件夹和收藏夹快速拖文件到其他软件，关闭主窗口后仍可使用。",
        ["settings.file_operations"] = "文件操作", ["settings.confirm_trash"] = "移到废纸篓前确认",
        ["settings.confirm_trash.description"] = "默认开启。关闭后直接移到废纸篓；远程删除、永久删除和清空废纸篓仍需确认。",
        ["settings.double_click_up"] = "双击空白处返回上级",
        ["settings.double_click_up.description"] = "默认关闭。开启后，双击文件区域背景返回上级；双击文件或文件夹仍打开项目。",
        ["settings.send_to"] = "发送到", ["settings.localsend"] = "LocalSend", ["settings.enable_localsend"] = "启用 LocalSend",
        ["settings.device_name"] = "设备名称", ["settings.receive_location"] = "接收位置",
        ["settings.appearance"] = "外观", ["settings.theme"] = "界面主题", ["settings.sidebar"] = "侧边栏",
        ["settings.search_locations"] = "搜索位置", ["settings.about"] = "关于",
        ["settings.cancel"] = "取消", ["settings.save"] = "保存", ["settings.remove"] = "移除",
        ["settings.offline"] = "离线或失效", ["settings.version"] = "版本 {0}",
        ["settings.access.empty"] = "选择需要浏览的文件夹；离线磁盘连接后可重试。",
        ["settings.access.ready"] = "授权包含所选文件夹及子目录。移除授权不会删除文件。",
        ["markdown.localization"] = "本地化",
        ["conversion.unknown_command"] = "未知的转换命令。", ["conversion.select_local_file"] = "请至少选择一个本地文件。",
        ["conversion.image_single_file"] = "图像转换每次只能处理一个文件。", ["conversion.local_only"] = "文件转换仅支持本地文件。",
        ["conversion.source_missing"] = "源文件不存在。", ["conversion.unsupported_file"] = "此文件不支持所选转换格式：{0}",
        ["conversion.to_format"] = "转为 {0}", ["conversion.processing"] = "正在转换 {0}",
        ["conversion.processing_batch"] = "正在转换 {0}（{1}/{2}）",
    };

    internal static readonly IReadOnlyDictionary<string, string> En = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["language.system"] = "Follow system", ["language.zh"] = "简体中文", ["language.en"] = "English",
        ["language.label"] = "Language", ["language.section"] = "Language",
        ["settings.general"] = "General", ["settings.general.description"] = "Make MacExplorer fit your daily workflow.",
        ["settings.system"] = "System", ["settings.language"] = "Language",
        ["settings.default_manager"] = "Set as default file manager",
        ["settings.default_manager.description"] = "Use MacExplorer when opening folders from the system and compatible apps. Restart your computer after changing this setting.",
        ["settings.copilot_enabled"] = "Enable Copilot", ["settings.copilot_enabled.description"] = "Show the AI assistant button when enabled. Turning it off closes the assistant panel.",
        ["settings.ai_analysis"] = "AI analysis", ["settings.ai_analysis.description"] = "Analyze image content and PDF text on this Mac. Online photo location lookup requires separate permission.",
        ["settings.privacy"] = "Privacy", ["settings.photo_location"] = "Look up photo locations online",
        ["settings.photo_location.description"] = "Send GPS coordinates to Apple to resolve a place name. Off by default; you can revoke access at any time.",
        ["settings.file_access"] = "File access", ["settings.choose_folder"] = "Choose folders…",
        ["settings.retry_access"] = "Retry access", ["settings.file_delivery"] = "File delivery",
        ["settings.file_delivery.title"] = "Show File Delivery in the menu bar",
        ["settings.file_delivery.description"] = "Quickly drag files from frequent folders and favorites to other apps. Available while the main window is closed.",
        ["settings.file_operations"] = "File operations", ["settings.confirm_trash"] = "Confirm before moving to Trash",
        ["settings.confirm_trash.description"] = "On by default. When off, items move directly to Trash. Remote deletion, permanent deletion, and emptying Trash still require confirmation.",
        ["settings.double_click_up"] = "Double-click empty space to go up",
        ["settings.double_click_up.description"] = "Off by default. Double-click the file area background to go to the parent folder. Files and folders still open normally.",
        ["settings.send_to"] = "Send to", ["settings.localsend"] = "LocalSend", ["settings.enable_localsend"] = "Enable LocalSend",
        ["settings.device_name"] = "Device name", ["settings.receive_location"] = "Receive location",
        ["settings.appearance"] = "Appearance", ["settings.theme"] = "Theme", ["settings.sidebar"] = "Sidebar",
        ["settings.search_locations"] = "Search locations", ["settings.about"] = "About",
        ["settings.cancel"] = "Cancel", ["settings.save"] = "Save", ["settings.remove"] = "Remove",
        ["settings.offline"] = "Unavailable", ["settings.version"] = "Version {0}",
        ["settings.access.empty"] = "Choose folders to browse. Retry after reconnecting an offline drive.",
        ["settings.access.ready"] = "Access includes the selected folders and their contents. Removing access does not delete files.",
        ["markdown.localization"] = "Localization",
        ["conversion.unknown_command"] = "Unknown conversion command.",
        ["conversion.select_local_file"] = "Select at least one local file.",
        ["conversion.image_single_file"] = "Image conversion processes one file at a time.",
        ["conversion.local_only"] = "Conversion supports local files only.", ["conversion.source_missing"] = "The source file does not exist.",
        ["conversion.unsupported_file"] = "This file does not support the selected format: {0}",
        ["conversion.to_format"] = "Convert to {0}", ["conversion.processing"] = "Converting {0}",
        ["conversion.processing_batch"] = "Converting {0} ({1}/{2})",
    };

    public static string Get(string key, CultureInfo culture)
    {
        if (Ui.TryGetValue(key, out var ui))
            return culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? ui.English : ui.Chinese;
        if (culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return En.TryGetValue(key, out var english) ? english : Zh.GetValueOrDefault(key, key);
        return Zh.GetValueOrDefault(key, key);
    }
}

public static class LocalizationSystem
{
    public static AppLanguage Resolve() => LocalizationService.ResolveSystemLanguage();
}
