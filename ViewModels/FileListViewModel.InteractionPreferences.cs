namespace MacExplorer.ViewModels;

public partial class FileListViewModel
{
    internal const string ConfirmBeforeTrashSettingKey = "confirm_before_trash";
    internal const string DoubleClickEmptyAreaGoUpSettingKey = "double_click_empty_area_go_up";

    private bool _confirmBeforeTrash = true;
    private bool _doubleClickEmptyAreaGoUp;

    // Read the shared settings cache at the point of use. Existing tabs/windows
    // must see edits made in another tab without being recreated or reloaded.
    public bool ConfirmBeforeTrash
    {
        get => _settingsService?.Get(ConfirmBeforeTrashSettingKey, true) ?? _confirmBeforeTrash;
        set
        {
            if (ConfirmBeforeTrash == value) return;
            _confirmBeforeTrash = value;
            _settingsService?.Set(ConfirmBeforeTrashSettingKey, value);
            OnPropertyChanged();
        }
    }

    public bool DoubleClickEmptyAreaGoUp
    {
        get => _settingsService?.Get(DoubleClickEmptyAreaGoUpSettingKey, false) ?? _doubleClickEmptyAreaGoUp;
        set
        {
            if (DoubleClickEmptyAreaGoUp == value) return;
            _doubleClickEmptyAreaGoUp = value;
            _settingsService?.Set(DoubleClickEmptyAreaGoUpSettingKey, value);
            OnPropertyChanged();
        }
    }
}
