using Avalonia.Input;

namespace MacExplorer.Services;

[Flags]
internal enum ShortcutScope { Browser = 1, Delivery = 2, Editor = 4, Dialog = 8, Text = 16, All = 31 }

internal sealed record ShortcutBinding(Key Key, KeyModifiers Modifiers)
{
    public bool Matches(KeyEventArgs e) => Key == e.Key && Modifiers == e.KeyModifiers;
    public KeyGesture Gesture => new(Key, Modifiers);
    public string Display =>
        (Modifiers.HasFlag(KeyModifiers.Control) ? "Control + " : "") +
        (Modifiers.HasFlag(KeyModifiers.Alt) ? "⌥" : "") +
        (Modifiers.HasFlag(KeyModifiers.Shift) ? "⇧" : "") +
        (Modifiers.HasFlag(KeyModifiers.Meta) ? "⌘" : "") + KeyDisplay;
    public string KeyDisplay => Key switch
        {
            Key.Back => "⌫", Key.Delete => "⌦", Key.Enter => "↩", Key.Space => "空格",
            Key.Tab => "Tab", Key.Escape => "Esc", Key.Up => "↑", Key.Down => "↓",
            Key.Left => "←", Key.Right => "→", Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]",
            Key.OemComma => ",", Key.OemPeriod => ".", Key.OemMinus => "−", Key.OemPlus => "=",
            Key.OemQuestion => "/", Key.OemSemicolon => ";", Key.OemQuotes => "'",
            Key.OemBackslash => "\\", Key.OemTilde => "`",
            >= Key.D0 and <= Key.D9 => ((int)Key - (int)Key.D0).ToString(),
            _ => Key.ToString()
        };
}

internal sealed record ShortcutDefinition(string Id, string Name, string Group, ShortcutScope Scope,
    IReadOnlyList<ShortcutBinding> Defaults, bool IsEditable = true, string? Context = null);

internal interface IShortcutService
{
    event Action? Changed;
    event Action? RecordingChanged;
    IReadOnlyList<ShortcutDefinition> Definitions { get; }
    IReadOnlyList<ShortcutBinding> GetBindings(string id);
    string GetDisplay(string id);
    bool Matches(string id, KeyEventArgs e);
    bool TrySet(string id, ShortcutBinding binding, out string? error);
    bool TryReset(string id, out string? error);
    void ResetAll();
    bool HintsEnabled { get; set; }
    bool IsRecording { get; }
    IDisposable BeginRecording(Action<Key, KeyModifiers> capture);
    bool Capture(Key key, KeyModifiers modifiers);
}

internal static class ShortcutIds
{
    public const string PageSearch = "search.page", GlobalSearch = "search.global", NewTab = "tab.new",
        CloseTab = "tab.close", PathInput = "navigation.path", Undo = "file.undo", Copy = "file.copy",
        Cut = "file.cut", Paste = "file.paste", SelectAll = "file.select-all", Open = "file.open",
        Refresh = "navigation.refresh", Info = "file.info", NewFile = "file.new", NewFolder = "file.new-folder",
        BatchRename = "file.batch-rename", CopyPath = "file.copy-path", Home = "navigation.home",
        Up = "navigation.up", Back = "navigation.back", Forward = "navigation.forward",
        PreviewPane = "view.preview", Trash = "file.trash", FullScreen = "window.fullscreen",
        NextTab = "fixed.next-tab", PreviousTab = "fixed.previous-tab";
}
