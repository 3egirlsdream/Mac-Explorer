using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MacExplorer.Services;

namespace MacExplorer.Controls;

internal sealed class ShortcutSettingsView : StackPanel
{
    private readonly IShortcutService _shortcuts;
    private readonly ToggleSwitch _hints;
    private readonly Dictionary<string, (Button Key, Dictionary<KeyModifiers, Button> Modifiers, TextBlock Error)> _rows = [];
    private IDisposable? _recording;
    private string? _recordingId;
    private Window? _window;
    private bool _subscribed;
    private bool _updating;

    internal ShortcutSettingsView(IShortcutService shortcuts)
    {
        _shortcuts = shortcuts;
        Spacing = 12;
        _hints = new ToggleSwitch { Classes = { "settings-toggle", "compact" }, IsChecked = shortcuts.HintsEnabled };
        AutomationProperties.SetName(_hints, "长按 Cmd 显示快捷键提示");
        var hintRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        hintRow.Children.Add(new TextBlock { Text = "长按 Cmd 显示快捷键提示", Classes = { "settings-label" }, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_hints, 1); hintRow.Children.Add(_hints);
        Children.Add(new Border { Classes = { "settings-group", "settings-compact-row" }, Child = hintRow });
        ToolTip.SetTip(hintRow, "单独按住 Cmd 700 毫秒显示全部快捷键；松开或执行操作后消失。");
        _hints.IsCheckedChanged += (_, _) => { if (!_updating) shortcuts.HintsEnabled = _hints.IsChecked == true; };

        foreach (var group in shortcuts.Definitions.GroupBy(d => d.Group))
        {
            var section = new StackPanel { Spacing = 6 };
            section.Children.Add(new TextBlock { Text = group.Key, Classes = { "settings-section-title" } });
            var rows = new StackPanel();
            var definitions = group.ToArray();
            for (var i = 0; i < definitions.Length; i++)
            {
                var definition = definitions[i];
                var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
                var name = new TextBlock { Text = definition.Name, VerticalAlignment = VerticalAlignment.Center,
                    Classes = { "settings-label" }, TextTrimming = TextTrimming.CharacterEllipsis };
                fields.Children.Add(name);
                if (definition.Context != null) ToolTip.SetTip(name, $"{definition.Context} · 固定快捷键");
                else ToolTip.SetTip(name, definition.Name);
                var keys = new Button { Classes = { "ghost", "compact", "shortcut-binding" },
                    Name = "ShortcutBinding_" + definition.Id.Replace('.', '_'), IsEnabled = definition.IsEditable };
                var keyGroup = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                var modifiers = new Dictionary<KeyModifiers, Button>();
                Grid.SetColumn(keyGroup, 1); fields.Children.Add(keyGroup);
                if (definition.IsEditable)
                {
                    var command = new Border { Classes = { "shortcut-fixed-key" },
                        Child = new ShortcutText { Text = "⌘", Classes = { "shortcut-symbol" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                    AutomationProperties.SetName(command, "Cmd 固定不可修改");
                    ToolTip.SetTip(command, "Cmd 固定不可修改");
                    keyGroup.Children.Add(command);
                    foreach (var (modifier, caption, label) in new[]
                    { (KeyModifiers.Control, "Control", "Control"), (KeyModifiers.Alt, "⌥", "Option"), (KeyModifiers.Shift, "⇧", "Shift") })
                    {
                        var button = new Button { Content = caption, Classes = { "ghost", "compact", "shortcut-modifier" },
                            Name = "ShortcutModifier_" + definition.Id.Replace('.', '_') + "_" + label };
                        AutomationProperties.SetName(button, $"{definition.Name} {label}");
                        button.Click += (_, _) =>
                        {
                            CancelRecording();
                            var current = shortcuts.GetBindings(definition.Id)[0];
                            shortcuts.TrySet(definition.Id, current with { Modifiers = current.Modifiers ^ modifier }, out var error);
                            ShowError(definition.Id, error);
                        };
                        modifiers[modifier] = button;
                        keyGroup.Children.Add(button);
                    }
                    keys.Classes.Add("shortcut-main-key");
                    AutomationProperties.SetName(keys, $"{definition.Name}主键");
                    keys.Click += (_, _) => StartRecording(definition.Id);
                    keys.LostFocus += (_, _) => { if (_recordingId == definition.Id) CancelRecording(); };
                    var reset = new Button { Classes = { "ghost", "compact", "icon-action" },
                        Content = new PathIcon { Data = Geometry.Parse(Assets.Icons.Refresh), Width = 14, Height = 14 } };
                    AutomationProperties.SetName(reset, $"恢复{definition.Name}默认快捷键");
                    ToolTip.SetTip(reset, "恢复默认");
                    reset.Click += (_, _) =>
                    {
                        CancelRecording();
                        shortcuts.TryReset(definition.Id, out var error);
                        ShowError(definition.Id, error);
                    };
                    Grid.SetColumn(reset, 2); fields.Children.Add(reset);
                }
                else AutomationProperties.SetName(keys, $"{definition.Name}快捷键");
                keyGroup.Children.Add(keys);
                var errorText = new TextBlock { Classes = { "shortcut-error" }, IsVisible = false, TextWrapping = TextWrapping.Wrap };
                var body = new StackPanel { Spacing = 2, Children = { fields, errorText } };
                var border = new Border { Classes = { "settings-compact-row" }, Child = body };
                if (i < definitions.Length - 1) border.Classes.Add("settings-divider");
                rows.Children.Add(border);
                _rows[definition.Id] = (keys, modifiers, errorText);
            }
            section.Children.Add(new Border { Classes = { "settings-group" }, Child = rows });
            Children.Add(section);
        }
        var resetAll = new Button { Content = "恢复全部默认", Classes = { "secondary", "compact" }, HorizontalAlignment = HorizontalAlignment.Right };
        resetAll.Click += (_, _) =>
        {
            CancelRecording(); shortcuts.ResetAll();
            foreach (var row in _rows.Values) row.Error.IsVisible = false;
        };
        Children.Add(resetAll);
        AttachedToVisualTree += (_, _) =>
        {
            if (!_subscribed) { shortcuts.Changed += RefreshRows; _subscribed = true; }
            _window = TopLevel.GetTopLevel(this) as Window;
            if (_window != null) _window.Deactivated += OnDeactivated;
            RefreshRows();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            CancelRecording();
            if (_subscribed) { shortcuts.Changed -= RefreshRows; _subscribed = false; }
            if (_window != null) _window.Deactivated -= OnDeactivated;
            _window = null;
        };
        RefreshRows();
    }

    private void OnDeactivated(object? sender, EventArgs e) => CancelRecording();
    private void StartRecording(string id)
    {
        CancelRecording();
        _recordingId = id;
        _rows[id].Error.IsVisible = false;
        _recording = _shortcuts.BeginRecording((key, _) =>
        {
            if (key == Key.Escape) { CancelRecording(); return; }
            if (Services.Impl.ShortcutService.IsModifier(key)) return;
            var modifiers = _shortcuts.GetBindings(id)[0].Modifiers;
            if (!_shortcuts.TrySet(id, new(key, modifiers), out var error)) { ShowError(id, error); return; }
            ShowError(id, null);
            CancelRecording();
        });
        RefreshRows();
        _rows[id].Key.Focus();
    }
    private void CancelRecording()
    {
        _recording?.Dispose(); _recording = null; _recordingId = null;
        RefreshRows();
    }
    private void ShowError(string id, string? error)
    {
        _rows[id].Error.Text = error; _rows[id].Error.IsVisible = error != null;
    }
    private void RefreshRows()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(RefreshRows); return; }
        _updating = true;
        _hints.IsChecked = _shortcuts.HintsEnabled;
        _updating = false;
        foreach (var definition in _shortcuts.Definitions)
        {
            var button = _rows[definition.Id].Key;
            var binding = _shortcuts.GetBindings(definition.Id)[0];
            button.Content = _recordingId == definition.Id ? "按键…" : definition.IsEditable
                ? binding.KeyDisplay : string.Join(" / ", _shortcuts.GetBindings(definition.Id).Select(b => b.Display));
            button.Classes.Set("recording", _recordingId == definition.Id);
            ToolTip.SetTip(button, definition.IsEditable
                ? string.Join(" / ", _shortcuts.GetBindings(definition.Id).Select(b => b.Display)) + " · 点击修改主键，Esc 取消"
                : definition.Context + " · 固定快捷键");
            foreach (var (modifier, modifierButton) in _rows[definition.Id].Modifiers)
            {
                var active = binding.Modifiers.HasFlag(modifier);
                modifierButton.Classes.Set("active", active);
                ToolTip.SetTip(modifierButton, $"{AutomationProperties.GetName(modifierButton)} · {(active ? "已启用，点击移除" : "未启用，点击添加")}");
            }
        }
    }
}
