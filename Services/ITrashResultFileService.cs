namespace MacExplorer.Services;

/// <summary>Optional provider capability; providers without it cannot offer accurate Trash undo.</summary>
public interface ITrashResultFileService
{
    Task<string?> TrashWithResultAsync(string path);
}

internal static class FileTrash
{
    internal static async Task<string?> MoveAsync(IFileService files, string path)
    {
        if (files is ITrashResultFileService provider) return await provider.TrashWithResultAsync(path);
        await files.DeleteAsync(path, true);
        return null;
    }
}
