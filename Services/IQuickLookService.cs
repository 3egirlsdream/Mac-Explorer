namespace MacExplorer.Services;

public interface IQuickLookService
{
    Task PreviewFileAsync(string filePath);
}
