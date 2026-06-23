using Avalonia;
using Avalonia.Styling;
using MacExplorer.Services;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public class MacThemeService : IThemeService
{
    private readonly ISettingsService _settingsService;
    private string _themeMode;

    public MacThemeService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        _themeMode = settingsService.Get("theme_mode", "system");
    }

    public bool IsDarkMode => _themeMode == "dark" || (_themeMode == "system" && DetectDarkMode());

    public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

    public void Initialize()
    {
        ApplyTheme();
    }

    public void SetThemeMode(string mode)
    {
        _themeMode = mode is "light" or "dark" ? mode : "system";
        _settingsService.Set("theme_mode", _themeMode);
        ApplyTheme();
        ThemeChanged?.Invoke(this, new ThemeChangedEventArgs { IsDarkMode = IsDarkMode });
    }

    public string GetThemeMode() => _themeMode;

    private void ApplyTheme()
    {
        if (Application.Current == null) return;
        Application.Current.RequestedThemeVariant = _themeMode switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    private static bool DetectDarkMode()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "defaults",
                Arguments = "read -g AppleInterfaceStyle",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(psi);
            var result = process?.StandardOutput.ReadToEnd() ?? "";
            return result.Contains("Dark");
        }
        catch { return false; }
    }
}
