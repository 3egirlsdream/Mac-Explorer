using Foundation;
using MacExplorer.Services;
using UIKit;
using CoreFoundation;

namespace MacExplorer.Platforms.MacCatalyst.Services;

/// <summary>
/// Mac Catalyst 主题服务实现，支持检测系统深色模式并允许用户覆盖
/// </summary>
public class MacThemeService : IThemeService
{
    private readonly ISettingsService _settingsService;
    private bool _isDarkMode;
    private bool _systemDarkMode;

    public bool IsDarkMode => _isDarkMode;

    public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

    public MacThemeService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public void Initialize()
    {
        _systemDarkMode = DetectSystemDarkMode();
        var themeMode = _settingsService.Get("theme_mode", "system");
        ApplyTheme(themeMode);

        NSNotificationCenter.DefaultCenter.AddObserver(
            new NSString("NSSystemAppearanceChangedNotification"),
            OnSystemAppearanceChanged);
    }

    /// <summary>
    /// 设置主题模式
    /// </summary>
    /// <param name="mode">"system" | "light" | "dark"</param>
    public void SetThemeMode(string mode)
    {
        _settingsService.Set("theme_mode", mode);
        ApplyTheme(mode);
    }

    /// <summary>
    /// 获取当前主题模式设置
    /// </summary>
    public string GetThemeMode()
    {
        return _settingsService.Get("theme_mode", "system");
    }

    private bool DetectSystemDarkMode()
    {
        var currentTraitCollection = UITraitCollection.CurrentTraitCollection;
        return currentTraitCollection.UserInterfaceStyle == UIUserInterfaceStyle.Dark;
    }

    private void OnSystemAppearanceChanged(NSNotification notification)
    {
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            var newSystemDarkMode = DetectSystemDarkMode();
            
            if (newSystemDarkMode != _systemDarkMode)
            {
                _systemDarkMode = newSystemDarkMode;

                var themeMode = _settingsService.Get("theme_mode", "system");
                if (themeMode == "system")
                {
                    ApplyTheme(themeMode);
                }
            }
        });
    }

    private void ApplyTheme(string themeMode)
    {
        bool newDarkMode = themeMode switch
        {
            "dark" => true,
            "light" => false,
            _ => _systemDarkMode
        };

        if (newDarkMode != _isDarkMode)
        {
            _isDarkMode = newDarkMode;
            ThemeChanged?.Invoke(this, new ThemeChangedEventArgs { IsDarkMode = _isDarkMode });
        }
    }
}
