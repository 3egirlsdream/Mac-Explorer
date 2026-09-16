namespace MacExplorer.Models;

/// <summary>
/// System actions persisted in open_with_apps via reserved bundle ids so they
/// share the settings list and menu placement with open-with applications.
/// </summary>
public static class BuiltInOpenWithActions
{
    public const string RevealInFinderBundleId = "fkfinder.builtin.reveal-in-finder";
    public const string OpenInTerminalBundleId = "fkfinder.builtin.open-in-terminal";

    public const string FinderAppPath = "/System/Library/CoreServices/Finder.app";
    public const string TerminalBundleId = "com.apple.Terminal";

    public const string RevealInFinderLabel = "在 Finder 中显示";
    public const string OpenInTerminalLabel = "在终端中打开";

    public static bool IsBuiltIn(string bundleId)
        => IsRevealInFinder(bundleId) || IsOpenInTerminal(bundleId);

    public static bool IsRevealInFinder(string bundleId)
        => string.Equals(bundleId, RevealInFinderBundleId, StringComparison.Ordinal);

    public static bool IsOpenInTerminal(string bundleId)
        => string.Equals(bundleId, OpenInTerminalBundleId, StringComparison.Ordinal);
}
