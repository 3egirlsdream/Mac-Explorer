using Avalonia.Platform.Storage;

namespace MacExplorer.Services;

internal static class AuthorizedStoragePicker
{
    public static async Task<IReadOnlyList<IStorageFolder>> OpenAuthorizedFolderPickerAsync(
        this IStorageProvider storage, FolderPickerOpenOptions options)
    {
        var folders = await storage.OpenFolderPickerAsync(options);
        foreach (var folder in folders) DirectoryAccess.Current.RememberSelection(folder.Path.LocalPath);
        return folders;
    }
    public static async Task<IReadOnlyList<IStorageFile>> OpenAuthorizedFilePickerAsync(
        this IStorageProvider storage, FilePickerOpenOptions options)
    {
        var files = await storage.OpenFilePickerAsync(options);
        foreach (var file in files) DirectoryAccess.Current.RememberSelection(file.Path.LocalPath);
        return files;
    }
    public static async Task<IStorageFile?> SaveAuthorizedFilePickerAsync(
        this IStorageProvider storage, FilePickerSaveOptions options)
    {
        var file = await storage.SaveFilePickerAsync(options);
        if (file != null) DirectoryAccess.Current.RememberSelection(file.Path.LocalPath);
        return file;
    }
}
