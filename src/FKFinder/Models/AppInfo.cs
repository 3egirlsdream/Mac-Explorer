namespace FKFinder.Models;

public class RegisteredApp
{
    public string Name { get; init; } = string.Empty;
    public string BundleIdentifier { get; init; } = string.Empty;
    public string IconPath { get; init; } = string.Empty;
    public bool IsDefault { get; init; }
}
