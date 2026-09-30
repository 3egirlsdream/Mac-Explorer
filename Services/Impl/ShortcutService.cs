using System.Text.Json;
using Avalonia.Input;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Services.Impl;

internal sealed class ShortcutService : IShortcutService, IDisposable
{
    internal const string BindingsKey = "shortcut_bindings_v1";
    internal const string HintsKey = "shortcut_hints_enabled";
    private readonly ISettingsService? _settings;
    private Dictionary<string, ShortcutBinding> _overrides = new(StringComparer.Ordinal);
    private Action<Key, KeyModifiers>? _capture;
    private static readonly ShortcutService Defaults = new(null);
    internal static IShortcutService Resolve() => App.Services?.GetService<IShortcutService>() ?? Defaults;
    public event Action? Changed;
    public event Action? RecordingChanged;
    public IReadOnlyList<ShortcutDefinition> Definitions { get; } = BuildDefinitions();
    public bool IsRecording => _capture != null;
    public bool HintsEnabled
    {
        get => _settings?.Get(HintsKey, true) ?? true;
        set => _settings?.Set(HintsKey, value);
    }

    public ShortcutService(ISettingsService? settings)
    {
        _settings = settings;
        Load();
        if (settings != null) settings.SettingChanged += OnSettingChanged;
    }

    public IReadOnlyList<ShortcutBinding> GetBindings(string id) => _overrides.TryGetValue(id, out var binding)
        ? new[] { binding } : Definitions.First(d => d.Id == id).Defaults;
    public string GetDisplay(string id) => GetBindings(id)[0].Display;
    public bool Matches(string id, KeyEventArgs e) => !IsRecording && GetBindings(id).Any(b => b.Matches(e));

    public bool TrySet(string id, ShortcutBinding binding, out string? error)
    {
        var definition = Definitions.First(d => d.Id == id);
        error = Validate(definition, binding);
        if (error != null) return false;
        var conflict = FindConflict(definition, new[] { binding });
        if (conflict != null) { error = $"已被“{conflict.Name}”占用"; return false; }
        _overrides[id] = binding;
        Save();
        return true;
    }

    public bool TryReset(string id, out string? error)
    {
        var definition = Definitions.First(d => d.Id == id);
        if (!definition.IsEditable) { error = "此快捷键不可修改"; return false; }
        var conflict = FindConflict(definition, definition.Defaults);
        error = conflict == null ? null : $"默认组合已被“{conflict.Name}”占用";
        if (conflict != null) return false;
        _overrides.Remove(id);
        Save();
        return true;
    }

    public void ResetAll() { _overrides.Clear(); Save(); }

    private ShortcutDefinition? FindConflict(ShortcutDefinition definition, IReadOnlyList<ShortcutBinding> bindings)
        => Definitions.FirstOrDefault(other => other.Id != definition.Id && (other.Scope & definition.Scope) != 0
            && GetBindings(other.Id).Any(bindings.Contains));

    private static string? Validate(ShortcutDefinition definition, ShortcutBinding binding)
    {
        if (!definition.IsEditable) return "此快捷键不可修改";
        if (!Enum.IsDefined(binding.Key) || binding.Key == Key.None || IsModifier(binding.Key)) return "请按下一个主键";
        if (binding.Key == Key.Escape) return "Esc 用于取消录制";
        if (!binding.Modifiers.HasFlag(KeyModifiers.Meta)) return "组合必须包含 Cmd";
        if ((binding.Modifiers & ~(KeyModifiers.Meta | KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift)) != 0)
            return "不支持此组合";
        if (IsSystemReserved(binding)) return "此组合由 macOS 使用";
        return null;
    }

    internal static bool IsModifier(Key key) => key is Key.LWin or Key.RWin or Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift;
    internal static bool IsTextEditingGesture(KeyEventArgs e) =>
        e.Key is Key.C or Key.X or Key.V or Key.A or Key.Z
        && (e.KeyModifiers is KeyModifiers.Meta or KeyModifiers.Control
            || e.Key == Key.Z && e.KeyModifiers is (KeyModifiers.Meta | KeyModifiers.Shift) or (KeyModifiers.Control | KeyModifiers.Shift));
    private static bool IsSystemReserved(ShortcutBinding b) => b.Key switch
    {
        Key.Tab => b.Modifiers is KeyModifiers.Meta or (KeyModifiers.Meta | KeyModifiers.Shift),
        Key.Space => b.Modifiers is KeyModifiers.Meta or (KeyModifiers.Meta | KeyModifiers.Control) or (KeyModifiers.Meta | KeyModifiers.Alt),
        Key.Q => b.Modifiers is (KeyModifiers.Meta | KeyModifiers.Control) or (KeyModifiers.Meta | KeyModifiers.Shift)
            or (KeyModifiers.Meta | KeyModifiers.Shift | KeyModifiers.Alt),
        Key.D => b.Modifiers == (KeyModifiers.Meta | KeyModifiers.Alt),
        Key.Escape => b.Modifiers == (KeyModifiers.Meta | KeyModifiers.Alt),
        Key.D3 or Key.D4 or Key.D5 or Key.D6 => b.Modifiers == (KeyModifiers.Meta | KeyModifiers.Shift)
            || b.Modifiers == (KeyModifiers.Meta | KeyModifiers.Shift | KeyModifiers.Control),
        Key.OemTilde => b.Modifiers is KeyModifiers.Meta or (KeyModifiers.Meta | KeyModifiers.Shift),
        _ => false
    };

    private void Save()
    {
        if (_settings != null) _settings.Set(BindingsKey, JsonSerializer.Serialize(_overrides));
        else Changed?.Invoke();
    }
    private void OnSettingChanged(string key)
    {
        if (key == BindingsKey) Load();
        if (key is BindingsKey or HintsKey) Changed?.Invoke();
    }
    private void Load()
    {
        _overrides = new(StringComparer.Ordinal);
        var json = _settings?.Get(BindingsKey);
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, ShortcutBinding>>(json);
            if (saved == null) return;
            foreach (var (id, binding) in saved)
                if (binding != null && Definitions.FirstOrDefault(d => d.Id == id) is { } definition
                    && Validate(definition, binding) == null) _overrides[id] = binding;
            // Validate the final set together, so a valid reassignment of a freed default survives restart.
            bool removed;
            do
            {
                removed = false;
                foreach (var id in _overrides.Keys.ToArray())
                {
                    var definition = Definitions.First(d => d.Id == id);
                    if (FindConflict(definition, GetBindings(id)) == null) continue;
                    _overrides.Remove(id);
                    removed = true;
                }
            } while (removed);
        }
        catch (JsonException ex) { System.Diagnostics.Trace.TraceWarning($"Shortcut settings: {ex.Message}"); }
    }

    public IDisposable BeginRecording(Action<Key, KeyModifiers> capture)
    {
        _capture = capture;
        RecordingChanged?.Invoke();
        return new RecordingSession(this, capture);
    }
    public bool Capture(Key key, KeyModifiers modifiers)
    {
        if (_capture == null) return false;
        _capture(key, modifiers);
        return true;
    }
    private sealed class RecordingSession(ShortcutService owner, Action<Key, KeyModifiers> capture) : IDisposable
    {
        public void Dispose()
        {
            if (owner._capture != capture) return;
            owner._capture = null;
            owner.RecordingChanged?.Invoke();
        }
    }
    public void Dispose()
    {
        if (_settings != null) _settings.SettingChanged -= OnSettingChanged;
        _capture = null;
    }

    private static IReadOnlyList<ShortcutDefinition> BuildDefinitions()
    {
        const KeyModifiers cmd = KeyModifiers.Meta, shift = KeyModifiers.Shift;
        const ShortcutScope browser = ShortcutScope.Browser, files = ShortcutScope.Browser | ShortcutScope.Delivery;
        var definitions = new List<ShortcutDefinition>();
        void Add(string id, string name, string group, ShortcutScope scope, Key key, KeyModifiers mods = cmd,
            params ShortcutBinding[] aliases) => definitions.Add(new(id, name, group, scope,
                new[] { new ShortcutBinding(key, mods) }.Concat(aliases).ToArray()));
        Add(ShortcutIds.PageSearch, "页面搜索", "搜索与页签", browser, Key.F);
        Add(ShortcutIds.GlobalSearch, "全局搜索", "搜索与页签", browser, Key.K, cmd, new ShortcutBinding(Key.F, cmd | shift));
        Add(ShortcutIds.NewTab, "新建页签", "搜索与页签", browser, Key.T);
        Add(ShortcutIds.CloseTab, "关闭页签", "搜索与页签", browser, Key.W);
        Add(ShortcutIds.PathInput, "编辑路径", "导航", browser, Key.L);
        foreach (var (id, name, key, mods) in new[]
        {
            (ShortcutIds.Home, "回到首页", Key.H, cmd | shift), (ShortcutIds.Up, "上级目录", Key.Up, cmd),
            (ShortcutIds.Back, "后退", Key.OemOpenBrackets, cmd), (ShortcutIds.Forward, "前进", Key.OemCloseBrackets, cmd),
            (ShortcutIds.Refresh, "刷新", Key.R, cmd),
            (ShortcutIds.Copy, "复制文件", Key.C, cmd), (ShortcutIds.Cut, "剪切文件", Key.X, cmd),
            (ShortcutIds.Paste, "粘贴文件", Key.V, cmd), (ShortcutIds.SelectAll, "全选文件", Key.A, cmd),
            (ShortcutIds.Open, "打开文件", Key.O, cmd), (ShortcutIds.Info, "查看文件信息", Key.I, cmd),
            (ShortcutIds.NewFile, "新建文本文件", Key.N, cmd), (ShortcutIds.NewFolder, "新建文件夹", Key.N, cmd | shift),
            (ShortcutIds.BatchRename, "批量重命名", Key.R, cmd | shift), (ShortcutIds.CopyPath, "复制路径", Key.C, cmd | shift),
            (ShortcutIds.Trash, "移到废纸篓", Key.Back, cmd), (ShortcutIds.PreviewPane, "切换预览面板", Key.P, cmd | shift)
        })
        {
            var group = id.StartsWith("navigation.") ? "导航" : "文件操作";
            var aliases = new List<ShortcutBinding> { new ShortcutBinding(key, (mods & ~cmd) | KeyModifiers.Control) };
            if (id == ShortcutIds.Forward) aliases.Add(new ShortcutBinding(Key.Right, cmd));
            Add(id, name, group, files, key, mods, aliases.ToArray());
        }
        Add(ShortcutIds.Undo, "撤销文件操作", "文件操作", browser, Key.Z);
        Add(ShortcutIds.FullScreen, "切换全屏", "窗口", ShortcutScope.All, Key.F, cmd | KeyModifiers.Control);
        void Fixed(string id, string name, string group, ShortcutScope scope, string context, params ShortcutBinding[] keys)
            => definitions.Add(new(id, name, group, scope, keys, false, context));
        Fixed(ShortcutIds.NextTab, "下一个页签", "固定操作", browser, "浏览窗口", new ShortcutBinding(Key.Tab, KeyModifiers.Control));
        Fixed(ShortcutIds.PreviousTab, "上一个页签", "固定操作", browser, "浏览窗口", new ShortcutBinding(Key.Tab, KeyModifiers.Control | shift));
        Fixed("fixed.rename", "重命名／批量重命名", "固定操作", browser, "文件列表", new ShortcutBinding(Key.Enter, 0));
        Fixed("fixed.preview", "预览文件", "固定操作", files, "文件列表", new ShortcutBinding(Key.Space, 0));
        Fixed("fixed.delete", "移到废纸篓", "固定操作", browser, "文件列表", new ShortcutBinding(Key.Delete, 0));
        Fixed("fixed.up", "上级目录", "固定操作", browser, "文件列表", new ShortcutBinding(Key.Back, 0));
        Fixed("fixed.selection", "移动选择／焦点", "固定操作", ShortcutScope.All, "列表与控件", new ShortcutBinding(Key.Up, 0), new ShortcutBinding(Key.Down, 0), new ShortcutBinding(Key.Left, 0), new ShortcutBinding(Key.Right, 0));
        Fixed("fixed.escape", "关闭弹层／取消", "固定操作", ShortcutScope.All, "当前窗口", new ShortcutBinding(Key.Escape, 0));
        Fixed("native.hide", "隐藏应用", "窗口", ShortcutScope.All, "macOS", new ShortcutBinding(Key.H, cmd));
        Fixed("native.hide-others", "隐藏其他应用", "窗口", ShortcutScope.All, "macOS", new ShortcutBinding(Key.Q, cmd | KeyModifiers.Alt));
        Fixed("native.quit", "退出应用", "窗口", ShortcutScope.All, "macOS", new ShortcutBinding(Key.Q, cmd));
        foreach (var (id, name, key, mods) in new[]
        {
            ("save", "保存", Key.S, cmd), ("save-as", "另存为", Key.S, cmd | shift),
            ("close", "关闭文档", Key.W, cmd), ("find", "查找", Key.F, cmd),
            ("find-next", "下一个匹配", Key.G, cmd), ("find-previous", "上一个匹配", Key.G, cmd | shift),
            ("bold", "粗体", Key.B, cmd), ("italic", "斜体", Key.I, cmd), ("link", "插入链接", Key.K, cmd),
            ("copy", "复制文字", Key.C, cmd), ("cut", "剪切文字", Key.X, cmd), ("paste", "粘贴文字", Key.V, cmd),
            ("all", "全选文字", Key.A, cmd), ("undo", "撤销编辑", Key.Z, cmd), ("redo", "重做编辑", Key.Z, cmd | shift)
        })
        {
            var textEditing = id is "copy" or "cut" or "paste" or "all" or "undo" or "redo";
            Fixed("editor." + id, name, "文本编辑", textEditing ? ShortcutScope.Editor | ShortcutScope.Text : ShortcutScope.Editor,
                textEditing ? "输入框／编辑器" : "Markdown 编辑器", new ShortcutBinding(key, mods));
        }
        return definitions.AsReadOnly();
    }
}
