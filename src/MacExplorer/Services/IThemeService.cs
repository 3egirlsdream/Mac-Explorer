namespace MacExplorer.Services;

public interface IThemeService
{
    bool IsDarkMode { get; }
    event EventHandler<ThemeChangedEventArgs>? ThemeChanged;
    void Initialize();
    void SetThemeMode(string mode);
    string GetThemeMode();
}

public class ThemeChangedEventArgs : EventArgs
{
    public bool IsDarkMode { get; init; }
}
