namespace FKFinder.Services;

public interface IDefaultAppService
{
    bool IsDefaultFolderHandler();
    (bool Success, string Message) SetAsDefaultFolderHandler();
    (bool Success, string Message) ResetDefaultFolderHandler();
}
