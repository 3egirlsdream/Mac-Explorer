using MacExplorer.Models;

namespace MacExplorer.Services;

public interface IOpenWithAppService
{
    Task<List<OpenWithApp>> GetAllAsync();
    Task<List<OpenWithApp>> GetTopLevelAppsAsync();
    Task<List<OpenWithApp>> GetSubmenuAppsAsync();
    Task AddAsync(string bundleId, string label, bool isTopLevel);
    Task UpdateAsync(int id, string? label, bool? isTopLevel, int? sortOrder);
    Task RemoveAsync(int id);
    Task<List<AppListItem>> GetInstalledAppsAsync();
}
