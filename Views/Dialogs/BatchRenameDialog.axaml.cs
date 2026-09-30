using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views.Dialogs;

public partial class BatchRenameDialog : DialogWindow
{
    private readonly ObservableCollection<BatchRenameRule> _rules = [];
    private readonly HashSet<string> _excluded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _manualNames = new(StringComparer.Ordinal);
    private readonly List<BatchRenamePreset> _savedPresets = [];
    private IReadOnlyList<FileSystemEntry> _entries = [];
    private List<BatchRenamePreviewRow> _rows = [];
    private BatchRenameOptions _options = new();
    private BatchRenamePlan? _plan;
    private IBatchRenameService? _rename;
    private BatchRenameOperationService? _operation;
    private ISettingsService? _settings;
    private IFileOperationHistoryService? _history;
    private FileListViewModel? _viewModel;
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _executeCts;
    private DateTime _capturedAt;
    private bool _ready, _changing, _executing, _completed, _narrow;
    private bool _multipleDirectories;
    private int _previewVersion;
    private Guid? _historyId;
    private List<BatchRenamePreviewItem> _pendingItems = [];
    private readonly HashSet<string> _successfulPaths = new(StringComparer.Ordinal);
    private object? _dragItem;
    private Point _dragStart;
    private bool _dragMoved;
    private readonly Dictionary<NumericUpDown, string> _numericErrors = [];
    private bool HasNumericErrors => _numericErrors.Keys.Any(input => input.IsEffectivelyVisible);
    private readonly HashSet<string> _failedPaths = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingPaths = new(StringComparer.Ordinal);
    private StackPanel? _fieldHost;
    private readonly List<(Control Control, Func<bool> Visible)> _conditionalFields = [];

    public BatchRenameDialog()
    {
        InitializeComponent();
        RuleList.ItemsSource = _rules;
        foreach (var list in new[] { RuleList, PreviewList })
        {
            list.AddHandler(PointerPressedEvent, OnReorderPressed, RoutingStrategies.Tunnel);
            list.AddHandler(PointerMovedEvent, OnReorderMoved, RoutingStrategies.Tunnel);
            list.AddHandler(PointerReleasedEvent, OnReorderReleased, RoutingStrategies.Tunnel);
        }
        SizeChanged += (_, _) => AdaptLayout();
        // Run before DialogWindow's Escape handler, including Escape inside a name cell.
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Closing += (_, e) => { if (_executing) { e.Cancel = true; _executeCts?.Cancel(); } };
        Closed += (_, _) =>
        {
            _ready = false; _previewCts?.Cancel(); _executeCts?.Cancel();
            foreach (var rule in _rules) rule.PropertyChanged -= OnRuleChanged;
        };
    }

    public Task<bool> ShowDialogAsync(Window owner, FileListViewModel viewModel, BatchRenameRequest? request = null)
    {
        _viewModel = viewModel;
        Configure(App.Services.GetRequiredService<IBatchRenameService>(),
            App.Services.GetRequiredService<BatchRenameOperationService>(),
            App.Services.GetService<ISettingsService>(), App.Services.GetService<IFileOperationHistoryService>(),
            request ?? viewModel.CreateBatchRenameRequest());
        var screen = owner.Screens.ScreenFromWindow(owner);
        if (screen != null)
        {
            Width = Math.Min(1160, Math.Max(MinWidth, screen.WorkingArea.Width / screen.Scaling - 48));
            Height = Math.Min(720, Math.Max(MinHeight, screen.WorkingArea.Height / screen.Scaling - 64));
        }
        return base.ShowDialog<bool>(owner);
    }

    internal void Configure(IBatchRenameService rename, BatchRenameOperationService operation,
        ISettingsService? settings, IFileOperationHistoryService? history, BatchRenameRequest request)
    {
        _rename = rename; _operation = operation; _settings = settings; _history = history;
        request = request.Snapshot(); _entries = request.Entries; _options = request.Options;
        _multipleDirectories = _entries.Select(entry => Path.GetDirectoryName(entry.FullPath)).Distinct(StringComparer.Ordinal).Take(2).Count() > 1;
        _capturedAt = request.CapturedAt;
        _excluded.UnionWith(request.ExcludedPaths);
        foreach (var pair in request.ManualNames) _manualNames[pair.Key] = pair.Value;
        _changing = true;
        foreach (var rule in request.Rules.Count > 0 ? request.Rules : [new BatchRenameRule()])
        {
            if (rule.ApplyToExtension) { rule.Target = RenameTarget.FullName; rule.ApplyToExtension = false; }
            AddRule(rule);
        }
        SortCombo.SelectedIndex = (int)_options.Sort; DescendingToggle.IsChecked = _options.Descending;
        PhotoSortFallbackCheck.IsChecked = _options.PhotoSortFallbackToModified;
        UpdateSortDirection();
        LoadPresets(); RuleList.SelectedIndex = 0;
        _changing = false; _ready = true;
        Title = $"批量重命名 · {_entries.Count} 项";
        AdaptLayout(); QueuePreview();
    }

    private void AddRule(BatchRenameRule rule)
    {
        rule.PropertyChanged += OnRuleChanged; _rules.Add(rule);
    }
    private void OnRuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_changing) return;
        if (e.PropertyName == nameof(BatchRenameRule.Type)) BuildEditor();
        UpdateParameterVisibility();
        if (e.PropertyName != nameof(BatchRenameRule.Label)) { PresetCombo.SelectedIndex = -1; QueuePreview(); }
    }
    private void OnRuleSelected(object? sender, SelectionChangedEventArgs e) { BuildEditor(); UpdateSteps(); }
    private void OnAddRule(object? sender, RoutedEventArgs e) { PresetCombo.SelectedIndex = -1; var rule = new BatchRenameRule(); AddRule(rule); RuleList.SelectedItem = rule; QueuePreview(); }
    private void OnDuplicateRule(object? sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedItem is not BatchRenameRule selected) return;
        PresetCombo.SelectedIndex = -1;
        var rule = selected.Clone(); rule.PropertyChanged += OnRuleChanged;
        _rules.Insert(_rules.IndexOf(selected) + 1, rule); RuleList.SelectedItem = rule; QueuePreview();
    }
    private void OnRemoveRule(object? sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedItem is not BatchRenameRule rule) return;
        PresetCombo.SelectedIndex = -1;
        var index = _rules.IndexOf(rule); rule.PropertyChanged -= OnRuleChanged; _rules.Remove(rule);
        if (_rules.Count == 0) AddRule(new BatchRenameRule());
        RuleList.SelectedIndex = Math.Min(index, _rules.Count - 1); QueuePreview();
    }
    private void OnMoveRuleUp(object? sender, RoutedEventArgs e) => MoveRule(-1);
    private void OnMoveRuleDown(object? sender, RoutedEventArgs e) => MoveRule(1);
    private void MoveRule(int delta)
    {
        var index = RuleList.SelectedIndex; var destination = index + delta;
        if (index < 0 || destination < 0 || destination >= _rules.Count || _executing || _completed) return;
        PresetCombo.SelectedIndex = -1;
        _rules.Move(index, destination); RuleList.SelectedIndex = destination; QueuePreview();
    }
    private void OnRuleListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key is Key.Up or Key.Down)
        { MoveRule(e.Key == Key.Up ? -1 : 1); e.Handled = true; }
    }

    private void BuildEditor()
    {
        if (RuleEditor == null || RuleList.SelectedItem is not BatchRenameRule rule) return;
        _numericErrors.Clear(); _conditionalFields.Clear(); RuleEditor.Children.Clear(); _fieldHost = RuleEditor;
        var type = new ComboBox { ItemsSource = Enum.GetValues<BatchRenameRuleType>().Select(t => new BatchRenameRule { Type = t }.Label).ToArray(), SelectedIndex = (int)rule.Type };
        type.Classes.Add("settings-inline-input");
        type.SelectionChanged += (_, _) => { if (type.SelectedIndex >= 0) rule.Type = (BatchRenameRuleType)type.SelectedIndex; };
        Field("规则", type);
        switch (rule.Type)
        {
            case BatchRenameRuleType.FindReplace:
                TextField("查找", rule, nameof(rule.FindText)); TextField("替换为", rule, nameof(rule.ReplaceText));
                CheckField("正则表达式", rule, nameof(rule.UseRegex)); CheckField("区分大小写", rule, nameof(rule.CaseSensitive));
                NumberField("匹配序号", rule, nameof(rule.MatchOccurrence), -1, 100000, "0 为全部，1 为第一处，-1 为最后一处");
                break;
            case BatchRenameRuleType.AddPrefix: TextField("前缀", rule, nameof(rule.PrefixText)); break;
            case BatchRenameRuleType.AddSuffix: TextField("后缀", rule, nameof(rule.SuffixText)); break;
            case BatchRenameRuleType.InsertText:
                TextField("文本", rule, nameof(rule.Text)); Placement(rule); break;
            case BatchRenameRuleType.RemoveText:
                NumberField("起始位置", rule, nameof(rule.Position), 0, 100000, "从 0 开始，按完整字符计算");
                NumberField("删除字符", rule, nameof(rule.RemoveLength), 0, 100000); break;
            case BatchRenameRuleType.Template:
                var template = new TextBox { TextWrapping = TextWrapping.Wrap, MinHeight = 48, MaxHeight = 96,
                    VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Top };
                template.Classes.Add("settings-inline-input");
                template.Bind(TextBox.TextProperty, new Binding(nameof(rule.TemplateText)) { Source = rule, Mode = BindingMode.TwoWay });
                ToolTip.SetTip(template, "{name} 原名称，{parent} 所在目录，{n} 编号，{taken:yyyyMMdd} 拍摄日期");
                AutomationProperties.SetName(template, "名称模板");
                var templateRow = new StackPanel { Spacing = 2 };
                templateRow.Children.Add(new TextBlock { Text = "名称模板", Classes = { "rename-heading" } });
                templateRow.Children.Add(template);
                var templateBorder = new Border { Child = templateRow }; templateBorder.Classes.Add("settings-compact-row"); templateBorder.Classes.Add("settings-divider");
                RuleEditor.Children.Add(templateBorder);
                var tokens = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
                foreach (var (label, value) in new[] { ("名称", "{name}"), ("目录", "{parent}"), ("编号", "{n}"), ("拍摄日期", "{taken:yyyyMMdd}"), ("修改日期", "{modified:yyyyMMdd}") })
                {
                    var button = new Button { Content = label }; button.Classes.Add("ghost"); button.Classes.Add("compact");
                    button.Click += (_, _) => rule.TemplateText += value; tokens.Children.Add(button);
                }
                Field("插入字段", tokens);
                ConditionalSection(() => TemplateHas(rule, "n", "编号"), () => NumberFields(rule));
                ConditionalSection(() => TemplateHas(rule, "date", "日期", "taken", "拍摄日期", "created", "创建日期", "modified", "修改日期"), () => DateFields(rule));
                break;
            case BatchRenameRuleType.Sequence: NumberFields(rule); Placement(rule); break;
            case BatchRenameRuleType.Date: DateFields(rule); Placement(rule); break;
            case BatchRenameRuleType.CaseConversion:
                EnumField("转换", rule, nameof(rule.CaseMode), new[] { "大写", "小写", "单词首字母大写" }); break;
            case BatchRenameRuleType.Cleanup:
                CheckField("统一分隔符", rule, nameof(rule.NormalizeSeparators));
                ConditionalSection(() => rule.NormalizeSeparators, () => TextField("分隔符", rule, nameof(rule.Separator))); break;
            case BatchRenameRuleType.Extension: TextField("扩展名", rule, nameof(rule.ExtensionText), "可省略句点；留空移除扩展名"); break;
        }
        var advanced = new StackPanel(); _fieldHost = advanced;
        if (rule.Type == BatchRenameRuleType.Cleanup)
        {
            CheckField("去首尾空白", rule, nameof(rule.TrimWhitespace));
            CheckField("合并空格", rule, nameof(rule.CollapseWhitespace));
        }
        if (rule.Type != BatchRenameRuleType.Extension)
            EnumField("作用部分", rule, nameof(rule.Target), new[] { "名称", "扩展名", "完整文件名" });
        EnumField("项目类型", rule, nameof(rule.AppliesTo), new[] { "文件与文件夹", "仅文件", "仅文件夹" });
        TextField("文件类型", rule, nameof(rule.FileExtensions), "留空为全部；多个扩展名用逗号分隔，如 jpg,png");
        var more = new Expander { Header = new TextBlock { Text = "更多选项", Classes = { "rename-heading" }, Margin = new Thickness(12, 4) },
            Content = advanced, Padding = new Thickness(12, 4, 12, 4),
            IsExpanded = rule.Target != RenameTarget.Name || rule.AppliesTo != RenameAppliesTo.All || rule.FileExtensions.Length > 0 };
        more.Classes.Add("compact-details"); RuleEditor.Children.Add(more); _fieldHost = RuleEditor;
        UpdateParameterVisibility();
        UpdatePreviewStatus();
    }
    private static bool TemplateHas(BatchRenameRule rule, params string[] names) =>
        Regex.Matches(rule.TemplateText, @"\{([^{}:]+)(?::[^{}]*)?\}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
            .Any(match => names.Contains(match.Groups[1].Value));
    private void ConditionalSection(Func<bool> visible, Action build)
    {
        var parent = _fieldHost!; var section = new StackPanel(); _fieldHost = section;
        build(); _fieldHost = parent; parent.Children.Add(section); _conditionalFields.Add((section, visible));
    }
    private void UpdateParameterVisibility()
    {
        foreach (var (control, visible) in _conditionalFields) control.IsVisible = visible();
    }
    private void NumberFields(BatchRenameRule rule)
    {
        NumberField("起始编号", rule, nameof(rule.SequenceStart), 0, int.MaxValue);
        NumberField("步长", rule, nameof(rule.SequenceStep), 1, 10000);
        ConditionalSection(() => rule.Type != BatchRenameRuleType.Template || rule.TemplateText.Contains("{n}") || rule.TemplateText.Contains("{编号}"),
            () => NumberField("位数", rule, nameof(rule.SequencePadding), 0, 12));
        ConditionalSection(() => _multipleDirectories || _options.RestartPerDirectory, () =>
        {
            var restart = new ComboBox { ItemsSource = new[] { "整批连续", "按目录重新开始" }, SelectedIndex = _options.RestartPerDirectory ? 1 : 0 };
            restart.Classes.Add("settings-inline-input");
            restart.SelectionChanged += (_, _) => { _options.RestartPerDirectory = restart.SelectedIndex == 1; PresetCombo.SelectedIndex = -1; UpdateParameterVisibility(); QueuePreview(); };
            Field("编号范围", restart);
        });
    }
    private void DateFields(BatchRenameRule rule)
    {
        ConditionalSection(() => rule.Type != BatchRenameRuleType.Template || TemplateHas(rule, "date", "日期"),
            () => EnumField("日期来源", rule, nameof(rule.DateSource), new[] { "当前时间", "创建时间", "修改时间", "拍摄时间" }));
        ConditionalSection(() => rule.Type != BatchRenameRuleType.Template || new[] { "date", "日期", "taken", "拍摄日期", "created", "创建日期", "modified", "修改日期" }
            .Any(field => rule.TemplateText.Contains("{" + field + "}")), () =>
        {
            var choices = new[] { "yyyy-MM-dd", "yyyyMMdd", "yyyyMMdd_HHmmss", "yyyy-MM-dd_HH-mm-ss", "自定义…" };
            var index = Array.IndexOf(choices, rule.DateFormat);
            var formats = new ComboBox { ItemsSource = choices, SelectedIndex = index >= 0 ? index : 4 };
            formats.Classes.Add("settings-inline-input");
            Field("日期格式", formats);
            ConditionalSection(() => formats.SelectedIndex == 4, () => TextField("自定义格式", rule, nameof(rule.DateFormat)));
            formats.SelectionChanged += (_, _) => { if (formats.SelectedIndex is >= 0 and < 4) rule.DateFormat = choices[formats.SelectedIndex]; UpdateParameterVisibility(); };
        });
        ConditionalSection(() => rule.DateSource == RenameDateSource.PhotoTaken && (rule.Type != BatchRenameRuleType.Template || TemplateHas(rule, "date", "日期"))
            || rule.Type == BatchRenameRuleType.Template && TemplateHas(rule, "taken", "拍摄日期"),
            () => CheckField("缺失时用修改时间", rule, nameof(rule.FallbackToModified)));
    }
    private void Placement(BatchRenameRule rule)
    {
        EnumField("放置位置", rule, nameof(rule.Placement), new[] { "名称之前", "名称之后", "指定位置" });
        ConditionalSection(() => rule.Placement == RenamePosition.AtPosition,
            () => NumberField("字符位置", rule, nameof(rule.Position), 0, 100000, "从 0 开始"));
        if (rule.Type != BatchRenameRuleType.InsertText) TextField("分隔符", rule, nameof(rule.Separator));
    }
    private void Field(string label, Control control)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*"), ColumnSpacing = 4 };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(control, 1); row.Children.Add(control);
        var border = new Border { Child = row }; border.Classes.Add("settings-compact-row"); border.Classes.Add("settings-divider");
        (_fieldHost ?? RuleEditor).Children.Add(border);
    }
    private void TextField(string label, BatchRenameRule rule, string property, string? tip = null)
    {
        var input = new TextBox(); input.Classes.Add("settings-inline-input");
        input.Bind(TextBox.TextProperty, new Binding(property) { Source = rule, Mode = BindingMode.TwoWay });
        ToolTip.SetTip(input, tip); AutomationProperties.SetName(input, label); Field(label, input);
    }
    private void NumberField(string label, BatchRenameRule rule, string property, decimal min, decimal max, string? tip = null)
    {
        var input = new NumericUpDown { Minimum = min, Maximum = max, Increment = 1, FormatString = "0", MinHeight = 28 };
        input.Classes.Add("compact");
        input.Bind(NumericUpDown.ValueProperty, new Binding(property) { Source = rule, Mode = BindingMode.TwoWay });
        input.PropertyChanged += (_, change) =>
        {
            if (change.Property != NumericUpDown.TextProperty) return;
            if (decimal.TryParse(input.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) && value >= min && value <= max)
                _numericErrors.Remove(input);
            else _numericErrors[input] = $"{label}需要 {min}–{max} 范围内的整数";
            UpdatePreviewStatus();
        };
        ToolTip.SetTip(input, tip); AutomationProperties.SetName(input, label); Field(label, input);
    }
    private void CheckField(string label, BatchRenameRule rule, string property)
    {
        var input = new CheckBox { Content = label };
        input.Bind(CheckBox.IsCheckedProperty, new Binding(property) { Source = rule, Mode = BindingMode.TwoWay });
        Field("", input);
    }
    private void EnumField(string label, BatchRenameRule rule, string property, string[] labels)
    {
        var info = typeof(BatchRenameRule).GetProperty(property)!;
        var input = new ComboBox { ItemsSource = labels, SelectedIndex = Convert.ToInt32(info.GetValue(rule)) };
        input.Classes.Add("settings-inline-input");
        input.SelectionChanged += (_, _) => { if (input.SelectedIndex >= 0) info.SetValue(rule, Enum.ToObject(info.PropertyType, input.SelectedIndex)); };
        AutomationProperties.SetName(input, label); Field(label, input);
    }

    private void OnOptionsChanged(object? sender, RoutedEventArgs e)
    {
        if (!_ready || _changing) return;
        PresetCombo.SelectedIndex = -1;
        _options.Sort = (RenameSort)SortCombo.SelectedIndex; _options.Descending = DescendingToggle.IsChecked == true;
        _options.PhotoSortFallbackToModified = PhotoSortFallbackCheck.IsChecked == true;
        UpdateSortDirection(); QueuePreview();
    }
    private void UpdateSortDirection() => SortDirectionIcon.Data = Geometry.Parse(_options.Descending ? Assets.Icons.SortDesc : Assets.Icons.SortAsc);
    private async void QueuePreview()
    {
        if (!_ready || _changing || _executing || _completed || _rename == null) return;
        _previewCts?.Cancel(); _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token; var version = ++_previewVersion;
        ApplyButton.IsEnabled = false; _plan = null; StatusButton.Content = "正在更新预览…";
        var request = new BatchRenameRequest { Entries = _entries, Rules = _rules.ToArray(), Options = _options,
            ExcludedPaths = _excluded, ManualNames = _manualNames, CapturedAt = _capturedAt }.Snapshot();
        try
        {
            await Task.Delay(150, token);
            var plan = await _rename.GeneratePreviewAsync(request, token);
            if (!_ready || token.IsCancellationRequested || version != _previewVersion) return;
            var selectedPath = (PreviewList.SelectedItem as BatchRenamePreviewRow)?.Item.OriginalPath;
            var expanded = _rows.Where(r => r.IsExpanded).Select(r => r.Item.OriginalPath).ToHashSet(StringComparer.Ordinal);
            _plan = plan;
            foreach (var row in _rows) row.PropertyChanged -= OnRowChanged;
            _rows = plan.Items.Select(i => new BatchRenamePreviewRow(i) { ShowDirectory = _multipleDirectories }).ToList();
            foreach (var row in _rows) { row.IsExpanded = expanded.Contains(row.Item.OriginalPath); row.PropertyChanged += OnRowChanged; }
            UpdateSteps(); RefreshRows(selectedPath);
            UpdatePreviewStatus();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == _previewVersion && _ready) StatusButton.Content = ex.Message; }
    }
    private void UpdatePreviewStatus()
    {
        if (_plan == null || _executing || _completed) return;
        var included = _plan.Items.Where(i => i.IsIncluded).ToArray();
        var changed = included.Count(i => i.IsChanged && !i.HasError && !i.HasConflict);
        var issues = included.Count(i => i.HasError || i.HasConflict);
        StatusButton.Content = HasNumericErrors ? _numericErrors.First(error => error.Key.IsEffectivelyVisible).Value
            : $"{included.Length} 项 · {changed} 项变化" + (issues > 0 ? $" · {issues} 项问题" : "");
        ApplyButton.Content = $"重命名 {changed} 项"; ApplyButton.IsEnabled = _plan.CanExecute && !HasNumericErrors;
        ExcludeConflictsButton.IsEnabled = ResolveConflictsButton.IsEnabled = included.Any(i => i.HasConflict);
        ConflictActions.IsVisible = !_completed && included.Any(i => i.HasConflict);
        PhotoSortFallbackCheck.IsVisible = _options.Sort == RenameSort.PhotoTaken
            && (_options.PhotoSortFallbackToModified || included.Any(i => i.ErrorReason == "缺少用于排序的拍摄时间"));
    }
    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not BatchRenamePreviewRow row || e.PropertyName != nameof(row.IsIncluded) || _executing || _completed) return;
        if (row.IsIncluded) _excluded.Remove(row.Item.OriginalPath); else _excluded.Add(row.Item.OriginalPath);
        QueuePreview();
    }
    private void OnFilterChanged(object? sender, SelectionChangedEventArgs e) => RefreshRows();
    private void RefreshRows(string? selectedPath = null)
    {
        if (PreviewList == null) return;
        selectedPath ??= (PreviewList.SelectedItem as BatchRenamePreviewRow)?.Item.OriginalPath;
        var scroll = PreviewList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var offset = scroll?.Offset;
        PreviewList.ItemsSource = _rows.Where(row => FilterCombo.SelectedIndex switch
        {
            1 => row.Item.IsChanged, 2 => row.Item.HasConflict,
            3 => _completed && _failedPaths.Contains(row.Item.OriginalPath), 4 => row.HasIssue, _ => true
        }).ToArray();
        PreviewList.SelectedItem = _rows.FirstOrDefault(r => r.Item.OriginalPath == selectedPath);
        if (scroll != null && offset.HasValue) Dispatcher.UIThread.Post(() => scroll.Offset = offset.Value, DispatcherPriority.Loaded);
    }
    private void OnStepChanged(object? sender, SelectionChangedEventArgs e) => UpdateSteps(false);
    private void UpdateSteps(bool refreshChoices = true)
    {
        if (StepCombo == null) return;
        if (refreshChoices)
        {
            var selected = StepCombo.SelectedIndex;
            StepCombo.ItemsSource = new[] { "最终结果" }.Concat(_rules.Select((rule, index) => $"第 {index + 1} 步 · {rule.Label}")).ToArray();
            StepCombo.SelectedIndex = Math.Clamp(selected, 0, _rules.Count);
        }
        var step = StepCombo.SelectedIndex - 1;
        PreviewNameHeading.Text = step >= 0 ? $"第 {step + 1} 步结果" : "新名称";
        foreach (var row in _rows) row.UpdateSteps(_rules, RuleList.SelectedIndex, step);
    }
    private void OnStatusClick(object? sender, RoutedEventArgs e)
    { FilterCombo.SelectedIndex = _completed ? (_failedPaths.Count > 0 ? 3 : 0) : 4; PreviewList.Focus(); }
    private void OnExcludeConflicts(object? sender, RoutedEventArgs e)
    {
        foreach (var row in _rows.Where(r => r.Item.HasConflict)) _excluded.Add(row.Item.OriginalPath);
        QueuePreview();
    }
    private void OnResolveConflicts(object? sender, RoutedEventArgs e) { _options.ResolveConflicts = true; QueuePreview(); }

    private void OnEditName(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: BatchRenamePreviewRow row } control || !row.IsEditable) return;
        StepCombo.SelectedIndex = 0;
        row.IsEditing = true; row.EditedName = row.NewName;
        var parent = control.Parent as Panel;
        var editor = parent?.Children.OfType<TextBox>().FirstOrDefault();
        Dispatcher.UIThread.Post(() => { editor?.Focus(); editor?.SelectAll(); });
        e.Handled = true;
    }
    private void OnEditNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control { DataContext: BatchRenamePreviewRow row }) return;
        if (e.Key == Key.Enter) { CommitName(row); e.Handled = true; }
        else if (e.Key == Key.Escape) { row.IsEditing = false; row.EditedName = row.NewName; e.Handled = true; }
    }
    private void OnEditNameLostFocus(object? sender, RoutedEventArgs e)
    { if (sender is Control { DataContext: BatchRenamePreviewRow row } && row.IsEditing) CommitName(row); }
    private void CommitName(BatchRenamePreviewRow row)
    {
        row.IsEditing = false;
        if (row.EditedName == row.NewName) return;
        _manualNames[row.Item.OriginalPath] = row.EditedName; QueuePreview();
    }
    private void OnResetName(object? sender, RoutedEventArgs e)
    { if (sender is Control { DataContext: BatchRenamePreviewRow row }) { _manualNames.Remove(row.Item.OriginalPath); QueuePreview(); } }
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key is Key.Up or Key.Down)
        { MovePreview(e.Key == Key.Up ? -1 : 1); e.Handled = true; }
        else if (e.Key == Key.Enter && PreviewList.SelectedItem is BatchRenamePreviewRow row && row.IsEditable)
        {
            StepCombo.SelectedIndex = 0;
            row.IsExpanded = true;
            var container = PreviewList.ContainerFromItem(row) as Control;
            var editor = container?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            row.IsEditing = true; Dispatcher.UIThread.Post(() => { editor?.Focus(); editor?.SelectAll(); }); e.Handled = true;
        }
    }
    private void OnMovePreviewUp(object? sender, RoutedEventArgs e) => MovePreview(-1);
    private void OnMovePreviewDown(object? sender, RoutedEventArgs e) => MovePreview(1);
    private void MovePreview(int delta)
    {
        if (PreviewList.SelectedItem is not BatchRenamePreviewRow row || _executing || _completed) return;
        var ordered = _rows.Select(r => r.Item.Entry!).ToList();
        var index = ordered.FindIndex(e => e.FullPath == row.Item.OriginalPath); var destination = index + delta;
        if (index < 0 || destination < 0 || destination >= ordered.Count) return;
        var entry = ordered[index]; ordered.RemoveAt(index); ordered.Insert(destination, entry);
        _entries = ordered; _options.Sort = RenameSort.Manual; SortCombo.SelectedIndex = (int)RenameSort.Manual; QueuePreview();
    }

    private void OnReorderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_executing || _completed || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || sender is not ListBox list) return;
        var control = e.Source as Control;
        if (control is TextBox or Button or CheckBox || control?.FindAncestorOfType<TextBox>() != null
            || control?.FindAncestorOfType<Button>() != null || control?.FindAncestorOfType<CheckBox>() != null) return;
        _dragItem = (control as ListBoxItem ?? control?.FindAncestorOfType<ListBoxItem>())?.DataContext;
        _dragStart = e.GetPosition(list); _dragMoved = false;
    }
    private void OnReorderMoved(object? sender, PointerEventArgs e)
    {
        if (_dragItem == null || sender is not ListBox list || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(list);
        if (!_dragMoved && Math.Abs(point.Y - _dragStart.Y) < 6) return;
        var hit = list.InputHitTest(point) as Control;
        var target = hit is ListBoxItem container ? container.DataContext : hit?.FindAncestorOfType<ListBoxItem>()?.DataContext;
        if (target == null || ReferenceEquals(target, _dragItem)) return;
        _dragMoved = true; PresetCombo.SelectedIndex = -1;
        if (_dragItem is BatchRenameRule rule && target is BatchRenameRule other)
        {
            _rules.Move(_rules.IndexOf(rule), _rules.IndexOf(other)); RuleList.SelectedItem = rule;
        }
        else if (_dragItem is BatchRenamePreviewRow row && target is BatchRenamePreviewRow otherRow)
        {
            var index = _rows.IndexOf(row); var destination = _rows.IndexOf(otherRow);
            if (index < 0 || destination < 0) return;
            _rows.RemoveAt(index); _rows.Insert(destination, row);
            _entries = _rows.Select(r => r.Item.Entry!).ToArray(); _options.Sort = RenameSort.Manual;
            _changing = true; SortCombo.SelectedIndex = (int)RenameSort.Manual; _changing = false;
            RefreshRows(row.Item.OriginalPath);
        }
        ApplyButton.IsEnabled = false; e.Handled = true;
    }
    private void OnReorderReleased(object? sender, PointerReleasedEventArgs e)
    { _dragItem = null; if (_dragMoved) QueuePreview(); _dragMoved = false; }

    private async void OnApply(object? sender, RoutedEventArgs e)
    {
        if (_executing || _operation == null || !_completed && (_plan?.CanExecute != true || HasNumericErrors)) return;
        foreach (var row in _rows.Where(r => r.IsEditing)) CommitName(row);
        if (!_completed && _plan?.CanExecute != true) return;
        var targets = _completed ? _pendingItems : _plan!.Items.ToList();
        if (targets.Count == 0) { Close(true); return; }
        _executing = true; _previewCts?.Cancel(); _executeCts = new CancellationTokenSource();
        SetEditingEnabled(false); ApplyButton.IsEnabled = false; ApplyButton.Content = "正在重命名…";
        CancelButton.Content = "停止"; ExecutionProgress.IsVisible = true; ExecutionProgress.Value = 0;
        var acceptingProgress = true;
        try
        {
            var result = await _operation.ExecuteAsync(targets, _executeCts.Token, _historyId,
                new Progress<BatchRenameProgress>(update => { if (!acceptingProgress) return; ExecutionProgress.Value = update.Percent; StatusButton.Content = $"已处理 {update.CompletedCount}/{update.TotalCount}"; }));
            acceptingProgress = false;
            _historyId = result.HistoryBatchId ?? _historyId;
            foreach (var item in result.SuccessfulItems) _successfulPaths.Add(item.OriginalPath);
            _pendingItems = targets.Where(i => i.IsIncluded && i.IsChanged && !i.HasError && !i.HasConflict && !_successfulPaths.Contains(i.OriginalPath)).ToList();
            _pendingPaths.Clear(); _pendingPaths.UnionWith(_pendingItems.Select(i => i.OriginalPath));
            var failures = result.FailedItems.ToDictionary(i => i.OriginalPath, i => i.ExecutionError, StringComparer.Ordinal);
            _failedPaths.Clear(); _failedPaths.UnionWith(failures.Keys);
            _completed = true;
            ConflictActions.IsVisible = PhotoSortFallbackCheck.IsVisible = false;
            foreach (var row in _rows)
            {
                row.ExecutionError = failures.GetValueOrDefault(row.Item.OriginalPath) ?? "";
                row.StatusText = _successfulPaths.Contains(row.Item.OriginalPath) ? "已重命名"
                    : _failedPaths.Contains(row.Item.OriginalPath) ? $"失败：{row.ExecutionError}"
                    : _pendingPaths.Contains(row.Item.OriginalPath) ? "已停止" : row.Item.Status;
            }
            StatusButton.Content = $"成功 {_successfulPaths.Count} 项" + (_pendingItems.Count > 0 ? $" · 未完成 {_pendingItems.Count} 项" : "")
                + (result.Warnings.Count > 0 ? $" · {result.Warnings.Count} 项同步提示" : "");
            ToolTip.SetTip(StatusButton, string.Join(Environment.NewLine, result.Errors.Concat(result.Warnings)));
            UndoButton.IsVisible = _historyId.HasValue;
            if (_viewModel != null) await _viewModel.RefreshAsync();
        }
        catch (OperationCanceledException) { StatusButton.Content = "已停止，尚未开始重命名"; }
        catch (Exception ex) { StatusButton.Content = ex.Message; ToolTip.SetTip(StatusButton, ex.Message); }
        finally
        {
            acceptingProgress = false; _executing = false; ExecutionProgress.IsVisible = false;
            CancelButton.Content = _completed ? "完成" : "取消";
            CancelButton.IsEnabled = true;
            if (_completed) { ApplyButton.Content = "重试未完成项"; ApplyButton.IsVisible = _pendingItems.Count > 0; ApplyButton.IsEnabled = true; RefreshRows(); }
            else { SetEditingEnabled(true); QueuePreview(); }
        }
    }
    private void SetEditingEnabled(bool enabled)
    {
        OptionsPanel.IsEnabled = RulesPanel.IsEnabled = EditorPanel.IsEnabled = enabled;
        ExcludeConflictsButton.IsEnabled = ResolveConflictsButton.IsEnabled = enabled;
        foreach (var row in _rows) row.IsEditable = enabled;
    }
    private async void OnUndo(object? sender, RoutedEventArgs e)
    {
        if (_historyId is not { } id || _history == null) return;
        UndoButton.IsEnabled = false;
        var undone = await _history.UndoBatchAsync(id);
        if (undone)
        {
            _historyId = null; UndoButton.IsVisible = false; ApplyButton.IsVisible = false;
            StatusButton.Content = "已撤销本次重命名";
            foreach (var row in _rows.Where(r => _successfulPaths.Contains(r.Item.OriginalPath))) row.StatusText = "已恢复原名";
            if (_viewModel != null) await _viewModel.RefreshAsync();
        }
        else { StatusButton.Content = "部分项目未能恢复，请检查原名称是否被占用"; UndoButton.IsEnabled = true; }
    }
    private void OnCancel(object? sender, RoutedEventArgs e)
    { if (_executing) { _executeCts?.Cancel(); CancelButton.IsEnabled = false; } else Close(_completed); }
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            var editing = _rows.FirstOrDefault(r => r.IsEditing);
            if (editing != null) { editing.IsEditing = false; editing.EditedName = editing.NewName; e.Handled = true; }
            else if (_executing) { _executeCts?.Cancel(); e.Handled = true; }
        }
        else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Meta) && ApplyButton.IsEnabled)
        { OnApply(this, e); e.Handled = true; }
    }
    private void AdaptLayout()
    {
        if (BodyGrid == null) return;
        var narrow = Width < 1030;
        if (_narrow == narrow && BodyGrid.ColumnDefinitions.Count == (narrow ? 2 : 3)) return;
        _narrow = narrow;
        BodyGrid.ColumnDefinitions = new ColumnDefinitions(narrow ? "280,*" : "190,280,*");
        BodyGrid.RowDefinitions = new RowDefinitions(narrow ? "170,*" : "*");
        Grid.SetRow(RulesPanel, 0); Grid.SetColumn(RulesPanel, 0);
        Grid.SetRow(EditorPanel, narrow ? 1 : 0); Grid.SetColumn(EditorPanel, narrow ? 0 : 1);
        Grid.SetColumn(PreviewPanel, narrow ? 1 : 2); Grid.SetRowSpan(PreviewPanel, narrow ? 2 : 1);
    }

    private void LoadPresets()
    {
        _savedPresets.Clear();
        if (_settings?.Get("batch-rename.presets") is string json)
        {
            try { _savedPresets.AddRange(JsonSerializer.Deserialize<List<BatchRenamePreset>>(json) ?? []); }
            catch (JsonException) { }
        }
        PresetCombo.ItemsSource = BuiltInPresets().Concat(_savedPresets).ToArray();
    }
    private static IEnumerable<BatchRenamePreset> BuiltInPresets()
    {
        yield return new BatchRenamePreset { Name = "照片日期编号", Rules = [new BatchRenameRule { Type = BatchRenameRuleType.Template, TemplateText = "照片_{taken:yyyyMMdd}_{n:000}" }], Options = new BatchRenameOptions { Sort = RenameSort.PhotoTaken } };
        yield return new BatchRenamePreset { Name = "项目编号", Rules = [new BatchRenameRule { Type = BatchRenameRuleType.Template, TemplateText = "项目_{n:000}" }] };
        yield return new BatchRenamePreset { Name = "清理下载名称", Rules = [new BatchRenameRule { Type = BatchRenameRuleType.Cleanup }] };
    }
    private void OnPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _changing || PresetCombo.SelectedItem is not BatchRenamePreset preset) return;
        _changing = true;
        foreach (var rule in _rules) rule.PropertyChanged -= OnRuleChanged;
        _rules.Clear(); foreach (var rule in preset.Rules) AddRule(rule.Clone());
        _options = preset.Options.Clone(); SortCombo.SelectedIndex = (int)_options.Sort; DescendingToggle.IsChecked = _options.Descending;
        PhotoSortFallbackCheck.IsChecked = _options.PhotoSortFallbackToModified;
        UpdateSortDirection();
        RuleList.SelectedIndex = 0; _changing = false; BuildEditor(); QueuePreview();
    }
    private async void OnSavePreset(object? sender, RoutedEventArgs e)
    {
        if (_settings == null) return;
        var dialog = new DialogWindow { Title = "保存预设", Width = 340, Height = 170 };
        dialog.Resources["TitleBarBackgroundBrush"] = Brushes.Transparent;
        var name = new TextBox { PlaceholderText = "预设名称" }; name.Classes.Add("settings-inline-input");
        var save = new Button { Content = "保存" }; save.Classes.Add("primary"); save.Classes.Add("compact");
        var cancel = new Button { Content = "取消" }; cancel.Classes.Add("secondary"); cancel.Classes.Add("compact");
        var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        actions.Children.Add(cancel); actions.Children.Add(save);
        var body = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(16), RowSpacing = 12 };
        body.Children.Add(name); Grid.SetRow(actions, 1); body.Children.Add(actions); dialog.Content = body;
        cancel.Click += (_, _) => dialog.Close(null);
        save.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text)) dialog.Close(name.Text.Trim()); };
        dialog.Opened += (_, _) => name.Focus();
        var presetName = await dialog.ShowDialog<string?>(this);
        if (presetName == null) return;
        var preset = new BatchRenamePreset { Name = presetName, Rules = _rules.Select(r => r.Clone()).ToList(), Options = _options.Clone() };
        _savedPresets.RemoveAll(p => p.Name == presetName); _savedPresets.Add(preset);
        _settings.Set("batch-rename.presets", JsonSerializer.Serialize(_savedPresets));
        _changing = true; PresetCombo.ItemsSource = BuiltInPresets().Concat(_savedPresets).ToArray(); PresetCombo.SelectedItem = preset; _changing = false;
    }
}
