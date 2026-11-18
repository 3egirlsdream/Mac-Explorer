namespace MacExplorer.Models;

// Set only for local directories that would otherwise receive a package presentation.
internal sealed record DirectoryPackage(bool IsPackage, string IconKey, string? ApplicationPath = null, long ApplicationSize = 0)
{
    internal static readonly DirectoryPackage Folder = new(false, "folder");
}
