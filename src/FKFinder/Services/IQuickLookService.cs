namespace FKFinder.Services;

public interface IQuickLookService
{
    Task PreviewFileAsync(string filePath);
}
