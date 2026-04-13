namespace FKFinder.Services;

public interface IDefaultAppService
{
    bool IsDefaultFolderHandler();
    void SetAsDefaultFolderHandler();
    void ResetDefaultFolderHandler();
}
