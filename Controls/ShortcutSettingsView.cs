using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.Controls;

internal sealed class ShortcutSettingsView : StackPanel
{
    private readonly IShortcutService _shortcuts;
    private readonly ToggleSwitch _hints;
    private readonly Dictionary<string, (Button Key, Dictionary<KeyModifiers, Button> Modifiers, TextBlock Error, TextBlock Name, Button? Reset)> _rows = [];
    private readonly List<(TextBlock Title, string Group)> _groups = [];
    private readonly List<Border> _fixedCommands = [];
    private readonly TextBlock _hintLabel;
    private readonly Grid _hintRow;
    private readonly Button _resetAll;
    private ILocalizationService? _language;
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
        AutomationProperties.SetName(_hints, MacExplorer.Services.Impl.LocalizationText.Get("长按 Cmd 显示快捷键提示"));
        var hintRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        _hintRow = hintRow;
        _hintLabel = new TextBlock { Classes = { "settings-label" }, VerticalAlignment = VerticalAlignment.Center };
        hintRow.Children.Add(_hintLabel);
        Grid.SetColumn(_hints, 1); hintRow.Children.Add(_hints);
        Children.Add(new Border { Classes = { "settings-group", "settings-compact-row" }, Child = hintRow });
        ToolTip.SetTip(hintRow, MacExplorer.Services.Impl.LocalizationText.Get("单独按住 Cmd 700 毫秒显示全部快捷键；松开或执行操作后消失。"));
        _hints.IsCheckedChanged += (_, _) => { if (!_updating) shortcuts.HintsEnabled = _hints.IsChecked == true; };

        foreach (var group in shortcuts.Definitions.GroupBy(d => d.Group))
        {
            var section = new StackPanel { Spacing = 6 };
            var groupTitle = new TextBlock { Classes = { "settings-section-title" } };
            _groups.Add((groupTitle, group.Key));
            section.Children.Add(groupTitle);
            var rows = new StackPanel();
            var definitions = group.ToArray();
            for (var i = 0; i < definitions.Length; i++)
            {
                var definition = definitions[i];
                var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
                var name = new TextBlock { Text = definition.DisplayName, VerticalAlignment = VerticalAlignment.Center,
                    Classes = { "settings-label" }, TextTrimming = TextTrimming.CharacterEllipsis };
                fields.Children.Add(name);
                if (definition.Context != null) ToolTip.SetTip(name, MacExplorer.Services.Impl.LocalizationText.Get("{0} · 固定快捷键", definition.DisplayContext));
                else ToolTip.SetTip(name, definition.DisplayName);
                var keys = new Button { Classes = { "ghost", "compact", "shortcut-binding" },
                    Name = "ShortcutBinding_" + definition.Id.Replace('.', '_'), IsEnabled = definition.IsEditable };
                var keyGroup = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                var modifiers = new Dictionary<KeyModifiers, Button>();
                Grid.SetColumn(keyGroup, 1); fields.Children.Add(keyGroup);
                Button? reset = null;
                if (definition.IsEditable)
                {
                    var command = new Border { Classes = { "shortcut-fixed-key" },
                        Child = new ShortcutText { Text = "⌘", Classes = { "shortcut-symbol" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                    AutomationProperties.SetName(command, LocalizationText.Get("Cmd 固定不可修改"));
                    ToolTip.SetTip(command, LocalizationText.Get("Cmd 固定不可修改"));
                    keyGroup.Children.Add(command);
                    _fixedCommands.Add(command);
                    foreach (var (modifier, caption, label) in new[]
                    { (KeyModifiers.Control, "Control", "Control"), (KeyModifiers.Alt, "⌥", "Option"), (KeyModifiers.Shift, "⇧", "Shift") })
                    {
                        var button = new Button { Content = caption, Classes = { "ghost", "compact", "shortcut-modifier" },
                            Name = "ShortcutModifier_" + definition.Id.Replace('.', '_') + "_" + label };
                        AutomationProperties.SetName(button, $"{definition.DisplayName} {label}");
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
                    AutomationProperties.SetName(keys, MacExplorer.Services.Impl.LocalizationText.Get("{0}主键", definition.DisplayName));
                    keys.Click += (_, _) => StartRecording(definition.Id);
                    keys.LostFocus += (_, _) => { if (_recordingId == definition.Id) CancelRecording(); };
                    reset = new Button { Classes = { "ghost", "compact", "icon-action" },
                        Content = new PathIcon { Data = Geometry.Parse(Assets.Icons.Refresh), Width = 14, Height = 14 } };
                    AutomationProperties.SetName(reset, MacExplorer.Services.Impl.LocalizationText.Get("恢复{0}默认快捷键", definition.DisplayName));
                    ToolTip.SetTip(reset, MacExplorer.Services.Impl.LocalizationText.Get("恢复默认"));
                    reset.Click += (_, _) =>
                    {
                        CancelRecording();
                        shortcuts.TryReset(definition.Id, out var error);
                        ShowError(definition.Id, error);
                    };
                    Grid.SetColumn(reset, 2); fields.Children.Add(reset);
                }
                else AutomationProperties.SetName(keys, MacExplorer.Services.Impl.LocalizationText.Get("{0}快捷键", definition.DisplayName));
                keyGroup.Children.Add(keys);
                var errorText = new TextBlock { Classes = { "shortcut-error" }, IsVisible = false, TextWrapping = TextWrapping.Wrap };
                var body = new StackPanel { Spacing = 2, Children = { fields, errorText } };
                var border = new Border { Classes = { "settings-compact-row" }, Child = body };
                if (i < definitions.Length - 1) border.Classes.Add("settings-divider");
                rows.Children.Add(border);
                _rows[definition.Id] = (keys, modifiers, errorText, name, reset);
            }
            section.Children.Add(new Border { Classes = { "settings-group" }, Child = rows });
            Children.Add(section);
        }
        var resetAll = _resetAll = new Button { Content = MacExplorer.Services.Impl.LocalizationText.Get("恢复全部默认"), Classes = { "secondary", "compact" }, HorizontalAlignment = HorizontalAlignment.Right };
        resetAll.Click += (_, _) =>
        {
            CancelRecording(); shortcuts.ResetAll();
            foreach (var row in _rows.Values) row.Error.IsVisible = false;
        };
        Children.Add(resetAll);
        AttachedToVisualTree += (_, _) =>
        {
            if (!_subscribed) { shortcuts.Changed += RefreshRows; _subscribed = true; }
            _language = LocalizationService.Current;
            if (_language != null) _language.LanguageChanged += RefreshRows;
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
            if (_language != null) _language.LanguageChanged -= RefreshRows;
            _language = null;
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
        _hintLabel.Text = LocalizationText.Get("长按 Cmd 显示快捷键提示");
        AutomationProperties.SetName(_hints, _hintLabel.Text);
        ToolTip.SetTip(_hintRow, LocalizationText.Get("单独按住 Cmd 700 毫秒显示全部快捷键；松开或执行操作后消失。"));
        _resetAll.Content = LocalizationText.Get("恢复全部默认");
        foreach (var command in _fixedCommands)
        {
            AutomationProperties.SetName(command, LocalizationText.Get("Cmd 固定不可修改"));
            ToolTip.SetTip(command, LocalizationText.Get("Cmd 固定不可修改"));
        }
        foreach (var (title, group) in _groups) title.Text = LocalizationText.Get(group);
        foreach (var definition in _shortcuts.Definitions)
        {
            var row = _rows[definition.Id];
            row.Name.Text = definition.DisplayName;
            ToolTip.SetTip(row.Name, definition.Context == null ? definition.DisplayName
                : LocalizationText.Get("{0} · 固定快捷键", definition.DisplayContext));
            AutomationProperties.SetName(row.Key, LocalizationText.Get(definition.IsEditable ? "{0}主键" : "{0}快捷键", definition.DisplayName));
            if (row.Reset is { } reset)
            {
                AutomationProperties.SetName(reset, LocalizationText.Get("恢复{0}默认快捷键", definition.DisplayName));
                ToolTip.SetTip(reset, LocalizationText.Get("恢复默认"));
            }
            var button = _rows[definition.Id].Key;
            var binding = _shortcuts.GetBindings(definition.Id)[0];
            button.Content = _recordingId == definition.Id ? LocalizationText.Get("按键…") : definition.IsEditable
                ? binding.KeyDisplay : string.Join(" / ", _shortcuts.GetBindings(definition.Id).Select(b => b.Display));
            button.Classes.Set("recording", _recordingId == definition.Id);
            ToolTip.SetTip(button, definition.IsEditable
                ? string.Join(" / ", _shortcuts.GetBindings(definition.Id).Select(b => b.Display)) + LocalizationText.Get(" · 点击修改主键，Esc 取消")
                : LocalizationText.Get("{0} · 固定快捷键", definition.DisplayContext));
            foreach (var (modifier, modifierButton) in _rows[definition.Id].Modifiers)
            {
                var modifierName = modifier switch { KeyModifiers.Control => "Control", KeyModifiers.Alt => "Option", _ => "Shift" };
                AutomationProperties.SetName(modifierButton, $"{definition.DisplayName} {modifierName}");
                var active = binding.Modifiers.HasFlag(modifier);
                modifierButton.Classes.Set("active", active);
                ToolTip.SetTip(modifierButton, $"{AutomationProperties.GetName(modifierButton)} · {(active ? LocalizationText.Get("已启用，点击移除") : LocalizationText.Get("未启用，点击添加"))}");
            }
        }
    }
}
