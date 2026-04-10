namespace FKFinder.Services;

public interface IIconService
{
    string GetIconKey(string extension, bool isDirectory);
    string GetIconPath(string iconKey);
}
