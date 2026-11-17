using Avalonia.Threading;

namespace MacExplorer.Services.Impl;

internal sealed class FolderIconStyleService : IDisposable
{
    private readonly ISettingsService _settings;
    private bool _disposed;

    public FolderIconStyleService(ISettingsService settings)
    {
        _settings = settings;
        settings.SettingChanged += OnSettingChanged;
        Apply();
    }

    private void OnSettingChanged(string key)
    {
        if (key != FolderPhotoCoverStyles.SettingKey) return;
        if (Dispatcher.UIThread.CheckAccess()) Apply(); else Dispatcher.UIThread.Post(Apply);
    }

    private void Apply()
    {
        if (!_disposed) SvgIconCache.SetFolderStyle(FolderPhotoCoverStyles.Read(_settings));
    }

    public void Dispose()
    {
        _disposed = true;
        _settings.SettingChanged -= OnSettingChanged;
    }
}
