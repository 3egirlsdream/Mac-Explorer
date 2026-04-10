namespace FKFinder.Services;

public interface IThemeService
{
    bool IsDarkMode { get; }
    event EventHandler<ThemeChangedEventArgs>? ThemeChanged;
    void Initialize();
}

public class ThemeChangedEventArgs : EventArgs
{
    public bool IsDarkMode { get; init; }
}
