using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using MacExplorer.Assets;
using MacExplorer.Controls;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views;

public partial class MainWindow
{
    private VerticalTabStripInteraction _verticalTabInteraction = null!;
    private bool _isVerticalTabsEnabled;
    private bool _isVerticalTabsPinned = true;
    private double _verticalTabsWidth = VerticalTabBarPreferences.DefaultExpandedWidth;

    internal bool IsVerticalTabsEnabled => _isVerticalTabsEnabled;
    internal bool IsVerticalTabsPinned => _isVerticalTabsPinned;

    private void InitializeVerticalTabBar()
    {
        _verticalTabInteraction = new VerticalTabStripInteraction(
            VerticalTabBarControl,
            CanInteractWithTabs,
            (item, anchor, after, ungroup) => _vm?.MoveStripItem(item, anchor, after, ungroup),
            ShowTabContextMenu,
            group => _vm?.ToggleGroup(group),
            ShowGroupContextMenu,
            tab => _ = CloseTabCoreAsync(tab));

        VerticalTabBarControl.CanCollapseHover = () =>
            !IsModalInteractionBlocked
            && !_verticalTabInteraction.IsPointerDown
            && !VerticalTabBarControl.IsResizing
            && _tabContextMenu?.IsOpen != true
            && !PaneLayoutPopup.IsOpen;
        VerticalTabBarControl.CanAcceptFileDrag = CanInteractWithTabs;

        VerticalTabBarControl.AddTabRequested += () => _ = ExecuteWindowCapabilityAsync("ui.new-tab", "{}");
        VerticalTabBarControl.CloseTabRequested += tab => _ = CloseTabCoreAsync(tab);
        VerticalTabBarControl.GroupNewTabRequested += group => _ = AddTabInGroupAsync(group);
        VerticalTabBarControl.GroupRenameRequested += group => _ = RenameGroupAsync(group);
        VerticalTabBarControl.GroupPresetsRequested += ShowGroupPresetsForControl;
        VerticalTabBarControl.PaneLayoutRequested += TogglePaneLayoutPopupForControl;
        VerticalTabBarControl.TaskPanelRequested += ToggleTaskPanel;
        VerticalTabBarControl.CopilotRequested += ToggleCopilotCore;
        VerticalTabBarControl.PinStateChanged += OnVerticalTabBarPinChanged;
        VerticalTabBarControl.ExpandedWidthPreviewChanged += OnVerticalTabBarWidthPreviewChanged;
        VerticalTabBarControl.ExpandedWidthCommitted += OnVerticalTabBarWidthCommitted;
        VerticalTabBarControl.BlankDragPressed += e => OnTitleBarBlankPointerPressed(VerticalTabBarControl, e);
        VerticalTabBarControl.BlankContextMenuRequested += ShowVerticalTabBarBlankContextMenu;

        PaneLayoutPopup.Closed += (_, _) => VerticalTabBarControl.CollapseHoverIfPointerOutside();
        SizeChanged += OnMainWindowSizeChangedForVerticalTabs;

        var settings = App.Services.GetRequiredService<ISettingsService>();
        settings.SettingChanged += OnVerticalTabSettingChanged;
        LoadVerticalTabSettings(settings);
        ApplyVerticalTabLayout(suppressHoverIfUnpinning: false);
    }

    private void DisposeVerticalTabBar()
    {
        App.Services.GetRequiredService<ISettingsService>().SettingChanged -= OnVerticalTabSettingChanged;
        SizeChanged -= OnMainWindowSizeChangedForVerticalTabs;
        _verticalTabInteraction.Dispose();
    }

    private bool CanInteractWithTabs()
        => _vm != null && !IsModalInteractionBlocked && !_shutdownStarted && !GlobalSearchOverlay.IsVisible;

    private void LoadVerticalTabSettings(ISettingsService settings)
    {
        _isVerticalTabsEnabled = VerticalTabBarPreferences.IsEnabled(settings);
        _isVerticalTabsPinned = VerticalTabBarPreferences.IsPinned(settings);
        _verticalTabsWidth = VerticalTabBarPreferences.GetWidth(settings);
    }

    private void OnVerticalTabSettingChanged(string key)
    {
        if (!VerticalTabBarPreferences.IsRelatedSetting(key)) return;
        if (Dispatcher.UIThread.CheckAccess()) SyncVerticalTabSettingsFromStore();
        else Dispatcher.UIThread.Post(SyncVerticalTabSettingsFromStore);
    }

    private void SyncVerticalTabSettingsFromStore()
    {
        if (_shutdownStarted) return;
        var settings = App.Services.GetRequiredService<ISettingsService>();
        var wasEnabled = _isVerticalTabsEnabled;
        var wasPinned = _isVerticalTabsPinned;
        LoadVerticalTabSettings(settings);
        if (wasEnabled == _isVerticalTabsEnabled && wasPinned == _isVerticalTabsPinned
            && Math.Abs(VerticalTabBarControl.ExpandedWidth - _verticalTabsWidth) < 0.5)
            return;
        ApplyVerticalTabLayout(suppressHoverIfUnpinning: wasPinned && !_isVerticalTabsPinned,
            animatePinTransition: wasEnabled && _isVerticalTabsEnabled && wasPinned != _isVerticalTabsPinned);
    }

    internal void SetVerticalTabsEnabled(bool enabled)
    {
        if (_isVerticalTabsEnabled == enabled) return;
        _isVerticalTabsEnabled = enabled;
        VerticalTabBarPreferences.SetEnabled(App.Services.GetRequiredService<ISettingsService>(), enabled);
        ApplyVerticalTabLayout(suppressHoverIfUnpinning: false);
    }

    internal void SetVerticalTabsPinned(bool pinned, bool animate = false)
    {
        if (_isVerticalTabsPinned == pinned) return;
        var unpinning = _isVerticalTabsPinned && !pinned;
        _isVerticalTabsPinned = pinned;
        VerticalTabBarPreferences.SetPinned(App.Services.GetRequiredService<ISettingsService>(), pinned);
        ApplyVerticalTabLayout(suppressHoverIfUnpinning: unpinning, animatePinTransition: animate);
    }

    private void OnVerticalTabBarPinChanged(bool pinned)
    {
        _isVerticalTabsPinned = pinned;
        UpdateVerticalTabSlotWidth();
        VerticalTabBarPreferences.SetPinned(App.Services.GetRequiredService<ISettingsService>(), pinned);
    }

    private void OnVerticalTabBarWidthPreviewChanged(double width)
    {
        _verticalTabsWidth = width;
        if (_isVerticalTabsEnabled && _isVerticalTabsPinned)
            VerticalTabSlot.Width = width;
    }

    private void OnVerticalTabBarWidthCommitted(double width)
    {
        _verticalTabsWidth = width;
        VerticalTabBarPreferences.SetWidth(App.Services.GetRequiredService<ISettingsService>(), width);
        UpdateVerticalTabSlotWidth();
    }

    private void OnMainWindowSizeChangedForVerticalTabs(object? sender, SizeChangedEventArgs e)
    {
        if (!_isVerticalTabsEnabled) return;
        var clamped = VerticalTabBarPreferences.ClampWidth(_verticalTabsWidth, e.NewSize.Width);
        VerticalTabBarControl.ClampWidthForWindow(e.NewSize.Width);
        if (_isVerticalTabsPinned)
            VerticalTabSlot.Width = clamped;
    }

    private void ApplyVerticalTabLayout(bool suppressHoverIfUnpinning, bool animatePinTransition = false)
    {
        _tabInteraction.Cancel();
        _verticalTabInteraction.Cancel();
        PaneLayoutPopup.IsOpen = false;
        _tabContextMenu?.Close();

        ShowTitleBar = !_isVerticalTabsEnabled;
        ExtendClientAreaTitleBarHeightHint = _isVerticalTabsEnabled ? 0 : 40;
        TabContentJoin.IsVisible = !_isVerticalTabsEnabled;
        VerticalTabSlot.IsVisible = _isVerticalTabsEnabled;
        VerticalTabBarControl.IsVisible = _isVerticalTabsEnabled;

        var windowWidth = Bounds.Width > 0 ? Bounds.Width : Width;
        var clampedWidth = VerticalTabBarPreferences.ClampWidth(_verticalTabsWidth, windowWidth);
        UpdateVerticalTabSlotWidth();
        VerticalTabBarControl.ApplyConfiguration(_isVerticalTabsPinned, clampedWidth, suppressHoverIfUnpinning, animate: animatePinTransition);

        PaneLayoutPopup.PlacementTarget = _isVerticalTabsEnabled ? VerticalTabBarControl.PaneLayoutButton : PaneLayoutButton;
        PaneLayoutPopup.Placement = _isVerticalTabsEnabled ? PlacementMode.RightEdgeAlignedBottom : PlacementMode.BottomEdgeAlignedRight;

        if (_isVerticalTabsEnabled)
            VerticalTabBarControl.RefreshGroupVisualClasses();
    }

    private void UpdateVerticalTabSlotWidth()
    {
        var windowWidth = Bounds.Width > 0 ? Bounds.Width : Width;
        var clampedWidth = VerticalTabBarPreferences.ClampWidth(_verticalTabsWidth, windowWidth);
        VerticalTabSlot.Width = _isVerticalTabsPinned ? clampedWidth : VerticalTabBarPreferences.CollapsedWidth;
    }

    private void ShowVerticalTabBarBlankContextMenu(Control target)
    {
        if (_vm == null) return;
        _tabContextMenu?.Close();
        var menu = new ContextMenu();

        var newTabItem = new MenuItem
        {
            Header = LocalizationText.Get("ui.066"),
            IsEnabled = !_closingTabs,
            Icon = CreateTabMenuIcon(Icons.TabAdd)
        };
        newTabItem.Click += async (_, e) =>
        {
            e.Handled = true;
            menu.Close();
            await ExecuteWindowCapabilityAsync("ui.new-tab", "{}");
        };
        menu.Items.Add(newTabItem);

        var pinItem = new MenuItem
        {
            Header = LocalizationText.Get(_isVerticalTabsPinned ? "tabs.vertical.collapse" : "tabs.vertical.pin"),
            Icon = CreateTabMenuIcon(_isVerticalTabsPinned ? Icons.PanelLeftContract : Icons.Pin)
        };
        pinItem.Click += (_, e) =>
        {
            e.Handled = true;
            menu.Close();
            SetVerticalTabsPinned(!_isVerticalTabsPinned, animate: true);
        };
        menu.Items.Add(pinItem);

        menu.Items.Add(new Separator());

        var disableItem = new MenuItem
        {
            Header = LocalizationText.Get("tabs.vertical.disable"),
            Icon = CreateTabMenuIcon(Icons.PanelLeft)
        };
        disableItem.Click += (_, e) =>
        {
            e.Handled = true;
            menu.Close();
            SetVerticalTabsEnabled(false);
        };
        menu.Items.Add(disableItem);

        OpenTabMenu(menu, target);
    }
}
