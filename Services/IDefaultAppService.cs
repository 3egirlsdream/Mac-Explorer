namespace MacExplorer.Services;

public interface IDefaultAppService
{
    bool IsDefaultFolderHandler();
    DefaultFolderHandlerStatus GetFolderHandlerStatus();
    (bool Success, string Message) SetAsDefaultFolderHandler();
    (bool Success, string Message) ResetDefaultFolderHandler();
}

public readonly record struct DefaultFolderHandlerStatus(bool? IsDefault, bool? RequiresRestart, bool IsPartial = false);
