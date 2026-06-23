using MacExplorer.Models;

namespace MacExplorer.Services;

public interface IOpenWithAppService
{
    Task<List<OpenWithApp>> GetAllAsync();
    Task<List<OpenWithApp>> GetTopLevelAppsAsync();
    Task<List<OpenWithApp>> GetSubmenuAppsAsync();
    Task<string?> GetAppIconBase64Async(string bundleId);
    Task<string?> GetAppIconBase64ByPathAsync(string appPath);
    Task AddAsync(string bundleId, string label, bool isTopLevel, string? iconBase64 = null);
    Task UpdateAsync(int id, string? label, bool? isTopLevel, int? sortOrder);
    Task RemoveAsync(int id);
    Task<List<AppListItem>> GetInstalledAppsAsync();
}
