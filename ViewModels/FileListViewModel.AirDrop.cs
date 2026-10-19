using MacExplorer.Services.Impl;

namespace MacExplorer.ViewModels;

public partial class FileListViewModel
{
    private async Task SendAirDropAsync(IReadOnlyList<string> paths)
    {
        try
        {
            await _airDropService!.SendAsync(_topLevelWindow, paths);
        }
        catch (Exception ex)
        {
            StatusText = ex is DllNotFoundException or EntryPointNotFoundException
                ? LocalizationText.Get("airdrop.unavailable") : ex.Message;
            if (_topLevelWindow is { IsVisible: true } owner)
                MacExplorer.Controls.AppToast.ShowError(owner, StatusText);
        }
    }
}
