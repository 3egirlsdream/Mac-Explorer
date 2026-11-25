using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using Icons = MacExplorer.Assets.Icons;

namespace MacExplorer.Views;

internal sealed record PreviewActionBarState
{
    public string? CounterText { get; init; }
    public string? FileName { get; init; }
    public string? MetaText { get; init; }
    public bool CompactIdentity { get; init; }
    public bool ShowStepButtons { get; init; }
    public bool CanStepPrevious { get; init; }
    public bool CanStepNext { get; init; }
    public bool ShowBranch { get; init; }
    public bool IsBranchExpanded { get; init; }
    public bool ShowPlayVideo { get; init; }
    public bool ShowZoom { get; init; }
    public bool IsZoomed { get; init; }
    public bool IsZoomEnabled { get; init; } = true;
    public bool ShowRotate { get; init; }
    public bool ShowCheckerboard { get; init; }
    public bool IsCheckerboardChecked { get; init; }
    public bool ShowCopyImage { get; init; }
    public bool ShowCopyText { get; init; }
    public bool IsCopyTextEnabled { get; init; } = true;
    public bool ShowFirstPage { get; init; }
    public bool IsFirstPageEnabled { get; init; } = true;
    public bool ShowExtract { get; init; }
    public bool ShowTag { get; init; }
    public bool ShowOpen { get; init; }
    public bool OpenUsesPlayIcon { get; init; }
    public bool ShowQuickLook { get; init; }
    public bool ShowInfoToggle { get; init; }
    public bool IsInfoVisible { get; init; }
    public bool ShowDelete { get; init; }
    public bool ShowReveal { get; init; }
    public bool ShowCopyPath { get; init; }
}

public sealed partial class PreviewActionBar : UserControl
{
    private bool _syncing;
    private double _availableWidth = double.PositiveInfinity;
    private PreviewActionBarState _state = new();

    public event Action? PreviousClicked;
    public event Action? NextClicked;
    public event Action? BranchClicked;
    public event Action? PlayVideoClicked;
    public event Action? ZoomClicked;
    public event Action? RotateClicked;
    public event Action<bool>? CheckerboardToggled;
    public event Action? CopyImageClicked;
    public event Action? CopyTextClicked;
    public event Action? FirstPageClicked;
    public event Action? ExtractClicked;
    public event Action? TagClicked;
    public event Action? OpenClicked;
    public event Action? QuickLookClicked;
    public event Action? InfoClicked;
    public event Action? DeleteClicked;
    public event Action? RevealClicked;
    public event Action? CopyPathClicked;
    public event Action? TagPopupClosed;

    internal bool IsTagPopupOpen => TagPopup.IsOpen;
    internal bool IsAnyPopupOpen => TagPopup.IsOpen || MorePopup.IsOpen;
    internal bool HasVisibleContent { get; private set; }

    public PreviewActionBar()
    {
        InitializeComponent();
        PreviousButton.Click += (_, _) => PreviousClicked?.Invoke();
        NextButton.Click += (_, _) => NextClicked?.Invoke();
        BranchButton.Click += (_, _) => BranchClicked?.Invoke();
        PlayVideoButton.Click += (_, _) => PlayVideoClicked?.Invoke();
        ZoomButton.Click += (_, _) => { if (!_syncing) ZoomClicked?.Invoke(); };
        RotateButton.Click += (_, _) => RotateClicked?.Invoke();
        CheckerButton.Click += (_, _) => { if (!_syncing) CheckerboardToggled?.Invoke(CheckerButton.IsChecked == true); };
        CopyImageButton.Click += (_, _) => CopyImageClicked?.Invoke();
        CopyTextButton.Click += (_, _) => CopyTextClicked?.Invoke();
        FirstPageButton.Click += (_, _) => FirstPageClicked?.Invoke();
        ExtractButton.Click += (_, _) => ExtractClicked?.Invoke();
        TagButton.Click += (_, _) => TagClicked?.Invoke();
        OpenButton.Click += (_, _) => OpenClicked?.Invoke();
        QuickLookButton.Click += (_, _) => QuickLookClicked?.Invoke();
        InfoButton.Click += (_, _) => { if (!_syncing) InfoClicked?.Invoke(); };
        DeleteButton.Click += (_, _) => DeleteClicked?.Invoke();
        MoreButton.Click += (_, _) => MorePopup.IsOpen = !MorePopup.IsOpen;
        MorePopup.Opened += (_, _) => MoreButton.Classes.Set("dropdown-open", true);
        MorePopup.Closed += (_, _) => MoreButton.Classes.Set("dropdown-open", false);
        RevealMenuItem.Click += (_, _) =>
        {
            MorePopup.IsOpen = false;
            RevealClicked?.Invoke();
        };
        CopyPathMenuItem.Click += (_, _) =>
        {
            MorePopup.IsOpen = false;
            CopyPathClicked?.Invoke();
        };
        TagPopup.Opened += (_, _) => TagButton.Classes.Set("dropdown-open", true);
        TagPopup.Closed += (_, _) =>
        {
            TagButton.Classes.Set("dropdown-open", false);
            TagHost.Content = null;
            TagPopupClosed?.Invoke();
        };
    }

    internal void ApplyState(PreviewActionBarState state)
    {
        _state = state;
        _syncing = true;
        try
        {
            CounterText.Text = state.CounterText ?? string.Empty;
            CounterPill.IsVisible = !string.IsNullOrWhiteSpace(state.CounterText);

            NameText.Text = state.FileName ?? string.Empty;
            ToolTip.SetTip(NameText, state.FileName);
            MetaText.Text = state.MetaText ?? string.Empty;

            PreviousButton.IsVisible = state.ShowStepButtons;
            PreviousButton.IsEnabled = state.CanStepPrevious;
            NextButton.IsVisible = state.ShowStepButtons;
            NextButton.IsEnabled = state.CanStepNext;
            BranchButton.IsVisible = state.ShowBranch;
            if (state.ShowBranch)
            {
                var branchLabel = LocalizationText.Get(state.IsBranchExpanded ? "preview.action.collapse" : "preview.action.expand");
                BranchIcon.Data = Geometry.Parse(state.IsBranchExpanded ? Icons.ChevronUp : Icons.ChevronDown);
                AutomationProperties.SetName(BranchButton, branchLabel);
                ToolTip.SetTip(BranchButton, branchLabel);
            }

            PlayVideoButton.IsVisible = state.ShowPlayVideo;
            ZoomButton.IsVisible = state.ShowZoom;
            ZoomButton.IsEnabled = state.IsZoomEnabled;
            ZoomButton.IsChecked = state.ShowZoom && state.IsZoomed;
            RotateButton.IsVisible = state.ShowRotate;
            CheckerButton.IsVisible = state.ShowCheckerboard;
            CheckerButton.IsChecked = state.ShowCheckerboard && state.IsCheckerboardChecked;
            CopyImageButton.IsVisible = state.ShowCopyImage;
            CopyTextButton.IsVisible = state.ShowCopyText;
            CopyTextButton.IsEnabled = state.IsCopyTextEnabled;
            FirstPageButton.IsVisible = state.ShowFirstPage;
            FirstPageButton.IsEnabled = state.IsFirstPageEnabled;
            ExtractButton.IsVisible = state.ShowExtract;
            TagButton.IsVisible = state.ShowTag;

            OpenButton.IsVisible = state.ShowOpen;
            if (state.ShowOpen)
            {
                OpenIcon.Data = Geometry.Parse(state.OpenUsesPlayIcon ? Icons.Play : Icons.Open);
                var openTip = LocalizationText.Get(state.OpenUsesPlayIcon ? "preview.action.player.tip" : "file_gallery.action.open_tip");
                ToolTip.SetTip(OpenButton, openTip);
            }

            QuickLookButton.IsVisible = state.ShowQuickLook;
            InfoButton.IsVisible = state.ShowInfoToggle;
            InfoButton.IsChecked = state.ShowInfoToggle && state.IsInfoVisible;
            DeleteButton.IsVisible = state.ShowDelete;

            RevealMenuItem.IsVisible = state.ShowReveal;
            CopyPathMenuItem.IsVisible = state.ShowCopyPath;
            MoreButton.IsVisible = state.ShowReveal || state.ShowCopyPath;
        }
        finally { _syncing = false; }

        UpdateResponsiveLayout();
    }

    internal void SetTags(IReadOnlyList<FileTag>? tags)
    {
        TagDotsPanel.Children.Clear();
        if (tags == null || tags.Count == 0)
        {
            TagDotsPanel.IsVisible = false;
            UpdateResponsiveLayout();
            return;
        }

        var names = new List<string>(tags.Count);
        foreach (var tag in tags.Take(5))
        {
            names.Add(tag.DisplayName);
            IBrush brush = Color.TryParse(tag.ColorHex, out var color)
                ? new SolidColorBrush(color)
                : Brushes.Gray;
            TagDotsPanel.Children.Add(new Border
            {
                Classes = { "gallery-tag-dot" },
                Background = brush
            });
        }
        ToolTip.SetTip(TagDotsPanel, string.Join(" · ", names));
        TagDotsPanel.IsVisible = true;
        UpdateResponsiveLayout();
    }

    internal void UpdateAvailableWidth(double width)
    {
        _availableWidth = width > 0 ? width : double.PositiveInfinity;
        UpdateResponsiveLayout();
    }

    internal void ToggleTagPopup(FileListViewModel viewModel, string filePath)
    {
        if (TagPopup.IsOpen)
        {
            TagPopup.IsOpen = false;
            return;
        }
        var tree = new TagTreePanel(viewModel, [filePath]);
        tree.CloseRequested += () => TagPopup.IsOpen = false;
        TagHost.Content = tree;
        TagPopup.IsOpen = true;
    }

    internal void CloseTagPopup()
    {
        if (TagPopup.IsOpen) TagPopup.IsOpen = false;
        if (MorePopup.IsOpen) MorePopup.IsOpen = false;
        TagHost.Content = null;
    }

    private void UpdateResponsiveLayout()
    {
        var compact = _state.CompactIdentity;
        var showName = !compact && _availableWidth >= 560 && !string.IsNullOrWhiteSpace(_state.FileName);
        var showMeta = !compact && _availableWidth >= 740 && !string.IsNullOrWhiteSpace(_state.MetaText);
        // When CompactIdentity is true (e.g. SuperPreview overlay or Gallery with InfoPanel open),
        // still show ultra-short meta (like dimensions / rotation) if there is plenty of width in overlay mode.
        if (compact && string.IsNullOrWhiteSpace(_state.FileName) && _availableWidth >= 600 && !string.IsNullOrWhiteSpace(_state.MetaText))
            showMeta = true;

        NameText.IsVisible = showName;
        MetaText.IsVisible = showMeta;
        IdentityStack.IsVisible = showName || showMeta;

        var hasContext = CounterPill.IsVisible || IdentityStack.IsVisible || TagDotsPanel.IsVisible;
        ContextCluster.IsVisible = hasContext;

        var hasActions = ActionButtons.Children.Any(c => c.IsVisible);
        ContextDivider.IsVisible = hasContext && hasActions;
        HasVisibleContent = hasContext || hasActions;
    }
}
