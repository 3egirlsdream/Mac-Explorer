using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Preview;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Area", "Preview")]
[Trait("Kind", "UI")]
public sealed class DiffPreviewTests : FileListViewModelTestBase
{
    private const string Patch = "--- a/旧.txt\r\n+++ b/新.txt\r\n@@ -1 +1 @@\r\n-旧\t  \r\n+新\t  \r\n";

    [AvaloniaTheory(Explicit = true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SharedPreviewShowsDiffWithoutTakingCompactFocusAndClearsOnSwitch(bool compact, bool gallery)
    {
        var root = Directory.CreateTempSubdirectory("FKFinder_DiffUI_");
        using var theme = new FastListTestTheme();
        try
        {
            var entry = new FileSystemEntry
            {
                FullPath = Path.Combine(root.FullName, "sample.PATCH"), Name = "sample.PATCH", Extension = ".PATCH"
            };
            await File.WriteAllTextAsync(entry.FullPath, Patch);
            var files = new FakeFileService(root.FullName);
            files.Seed(entry);
            await using var fixture = await TabCacheFixture.CreateAsync(entries: 0,
                configureServices: services => services.AddSingleton<IFileService>(files));
            var input = new TextBox();
            var preview = new SuperPreviewView(compact, gallery);
            var window = CreateWindow(new Grid { Children = { input, preview } });
            try
            {
                window.Show();
                input.Focus();
                if (gallery) Assert.True(await preview.OpenGalleryFileAsync(entry, CancellationToken.None));
                else await preview.OpenAsync(entry);
                window.UpdateLayout();
                var view = Assert.Single(preview.GetVisualDescendants().OfType<DiffPreviewView>());
                var editor = view.FindControl<TextEditor>("Editor")!;
                Assert.Equal(DiffComparison.Project(DiffPreviewDocument.Parse(Patch).Comparison!.Before), editor.Text);
                Assert.True(view.FindControl<TextEditor>("AfterEditor")!.IsVisible);
                Assert.True(editor.IsReadOnly);
                Assert.False(editor.WordWrap);
                Assert.False(preview.FindControl<Border>("PreviewListCard")!.IsVisible);
                if (compact) Assert.True(input.IsKeyboardFocusWithin);

                var next = preview.OpenAsync(entry);
                Assert.Equal(string.Empty, editor.Text);
                await next;
                var text = new FileSystemEntry { Name = "next.txt", FullPath = Path.Combine(root.FullName, "next.txt"), Extension = ".txt" };
                await File.WriteAllTextAsync(text.FullPath, "ordinary text");
                files.Seed(text);
                await preview.OpenAsync(text);
                Assert.Empty(preview.GetVisualDescendants().OfType<DiffPreviewView>());
                Assert.Equal("ordinary text", preview.FindControl<TextBox>("PreviewText")!.Text);

                var pending = preview.OpenAsync(entry);
                preview.Close();
                await pending;
                Assert.False(preview.IsVisible);
                Assert.Empty(preview.GetVisualDescendants().OfType<DiffPreviewView>());

                await File.WriteAllTextAsync(entry.FullPath, "");
                await preview.OpenAsync(entry);
                Assert.Equal("", Assert.Single(preview.GetVisualDescendants().OfType<DiffPreviewView>()).FindControl<TextEditor>("Editor")!.Text);
            }
            finally { preview.Close(); window.Close(); }
        }
        finally { root.Delete(true); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(Key.Space)]
    [InlineData(Key.Escape)]
    public async Task FocusedEditorClosesPreviewAndRestoresFocus(Key key)
    {
        var root = Directory.CreateTempSubdirectory("FKFinder_DiffKeys_");
        using var theme = new FastListTestTheme();
        try
        {
            var entry = new FileSystemEntry { Name = "a.diff", FullPath = Path.Combine(root.FullName, "a.diff") };
            await File.WriteAllTextAsync(entry.FullPath, Patch);
            var files = new FakeFileService(root.FullName);
            files.Seed(entry);
            await using var fixture = await TabCacheFixture.CreateAsync(entries: 0,
                configureServices: services => services.AddSingleton<IFileService>(files));
            var input = new TextBox();
            var preview = new SuperPreviewView { IsVisible = false };
            var window = CreateWindow(new Grid { Children = { input, preview } });
            try
            {
                window.Show();
                input.Focus();
                await preview.OpenAsync(entry);
                var editor = Assert.Single(preview.GetVisualDescendants().OfType<DiffPreviewView>()).FindControl<TextEditor>("Editor")!;
                editor.TextArea.Focus();
                window.KeyPress(key, RawInputModifiers.None, key == Key.Space ? PhysicalKey.Space : PhysicalKey.Escape, null);
                Assert.False(preview.IsVisible);
                Assert.True(input.IsKeyboardFocusWithin);
                Assert.Equal("", editor.Text);
            }
            finally { preview.Close(); window.Close(); }
        }
        finally { root.Delete(true); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NarrowPreviewCopiesProjectedSourceAndRefreshesColorsAcrossThemes(bool after)
    {
        using var theme = new FastListTestTheme();
        var view = new DiffPreviewView();
        var window = CreateWindow(view);
        window.Width = 280;
        window.RequestedThemeVariant = ThemeVariant.Light;
        var document = DiffPreviewDocument.Parse(Patch + new string('x', 4000));
        view.SetDocument(document);
        try
        {
            window.Show();
            window.UpdateLayout();
            var editor = view.FindControl<TextEditor>(after ? "AfterEditor" : "Editor")!;
            var rows = after ? document.Comparison!.After : document.Comparison!.Before;
            var projected = DiffComparison.Project(rows);
            editor.TextArea.Focus();
            var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
            window.KeyPress(Key.A, command, PhysicalKey.A, null);
            Assert.Equal(projected, editor.SelectedText);
            window.KeyPress(Key.C, command, PhysicalKey.C, null);
            await WaitForPreviewAsync(() => editor.SelectionLength == projected.Length);
            Assert.Equal((after ? "b/新.txt\r\n@@ -1 +1 @@\r\n新\t  \r\n" : "a/旧.txt\r\n@@ -1 +1 @@\r\n旧\t  \r\n") + new string('x', 4000), await window.Clipboard!.TryGetTextAsync());

            editor.Select(0, 0);
            editor.ScrollToHome();
            AssertColors();
            window.RequestedThemeVariant = ThemeVariant.Dark;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AssertColors();
            Assert.Equal(projected, editor.Text);
            var scroll = Assert.Single(editor.GetVisualDescendants().OfType<ScrollViewer>());
            Assert.True(scroll.Extent.Width > scroll.Viewport.Width);
            view.Clear();
            Assert.Equal("", editor.Text);

            void AssertColors()
            {
                editor.TextArea.TextView.EnsureVisualLines();
                foreach (var (line, token) in new[] { (1, "AccentBrush"), (2, "AccentBrush"), (3, after ? "SuccessBrush" : "DangerBrush") })
                {
                    var visual = editor.TextArea.TextView.GetVisualLine(line)!;
                    Assert.NotNull(visual);
                    Assert.True(view.TryFindResource(token, view.ActualThemeVariant, out var brush));
                    Assert.Equal(((ISolidColorBrush)brush!).Color,
                        ((ISolidColorBrush)visual.Elements.First().TextRunProperties.ForegroundBrush!).Color);
                }
            }
        }
        finally { view.Clear(); window.Close(); }
    }

    [AvaloniaFact(Explicit = true)]
    public void ComparisonScrollsBothWaysAndKeepsCorrespondingRowsAtTheSameHeight()
    {
        using var theme = new FastListTestTheme();
        var patch = "@@ -10,121 +20,120 @@\n-removed\n" + string.Concat(Enumerable.Repeat(" unchanged中文\n", 120));
        var view = new DiffPreviewView();
        view.SetDocument(DiffPreviewDocument.Parse(patch));
        var window = CreateWindow(view);
        window.Width = 360;
        window.Height = 260;
        try
        {
            window.Show();
            window.UpdateLayout();
            var before = view.FindControl<TextEditor>("Editor")!;
            var after = view.FindControl<TextEditor>("AfterEditor")!;
            Assert.Single(before.TextArea.LeftMargins);
            Assert.Single(after.TextArea.LeftMargins);
            Assert.True(before.TextArea.LeftMargins[0].Bounds.Width > 0);
            Assert.True(after.TextArea.LeftMargins[0].Bounds.Width > 0);

            var leftScroll = (IScrollable)before.TextArea.TextView;
            var rightScroll = (IScrollable)after.TextArea.TextView;
            leftScroll.Offset = new Vector(0, 220);
            window.UpdateLayout();
            Assert.True(leftScroll.Offset.Y > 0);
            Assert.Equal(leftScroll.Offset.Y, rightScroll.Offset.Y);
            rightScroll.Offset = new Vector(0, 480);
            window.UpdateLayout();
            Assert.Equal(rightScroll.Offset.Y, leftScroll.Offset.Y);
            var leftView = before.TextArea.TextView;
            var rightView = after.TextArea.TextView;
            leftView.EnsureVisualLines();
            rightView.EnsureVisualLines();
            Assert.Equal(leftView.VisualLines.First().FirstDocumentLine.LineNumber, rightView.VisualLines.First().FirstDocumentLine.LineNumber);
            Assert.Equal(leftView.VisualLines.First().VisualTop, rightView.VisualLines.First().VisualTop);
            Assert.Equal(leftView.VisualLines.First().Height, rightView.VisualLines.First().Height);

            before.SelectAll();
            before.Copy();
            view.Clear();
            Assert.Equal("", before.Text);
            Assert.Equal("", after.Text);
        }
        finally { view.Clear(); window.Close(); }
    }

    [AvaloniaFact(Explicit = true)]
    public void ScrollingToEndDoesNotLeaveAViewportOfBlankSpace()
    {
        using var theme = new FastListTestTheme();
        var view = new DiffPreviewView();
        var window = CreateWindow(view);
        window.Height = 280;
        try
        {
            window.Show();
            foreach (var comparison in new[] { true, false })
            {
                var text = comparison ? "@@ -1,100 +1,100 @@\n" : "";
                text += string.Concat(Enumerable.Repeat(comparison ? " context\n" : "raw text\n", 100));
                view.SetDocument(DiffPreviewDocument.Parse(text));
                window.UpdateLayout();
                foreach (var editor in new[] { view.FindControl<TextEditor>("Editor")!, view.FindControl<TextEditor>("AfterEditor")! })
                {
                    if (!editor.IsVisible) continue;
                    var scrollViewer = Assert.Single(editor.GetVisualDescendants().OfType<ScrollViewer>());
                    scrollViewer.ScrollToEnd();
                    window.UpdateLayout();
                    var textView = editor.TextArea.TextView;
                    textView.EnsureVisualLines();
                    var last = textView.GetVisualLine(editor.Document.LineCount);
                    Assert.NotNull(last);
                    var scroll = (IScrollable)textView;
                    var contentBottom = last.VisualTop + last.Height;
                    Assert.InRange(contentBottom - scroll.Offset.Y, scroll.Viewport.Height - 1, scroll.Viewport.Height + 1);
                }
            }
        }
        finally { view.Clear(); window.Close(); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task UnsupportedPatchStillCopiesOriginalWhitespaceAndNewlines()
    {
        using var theme = new FastListTestTheme();
        const string raw = "Unknown patch format\r\n+保留原文\t \r\n";
        var view = new DiffPreviewView();
        view.SetDocument(DiffPreviewDocument.Parse(raw));
        var window = CreateWindow(view);
        try
        {
            window.Show();
            var editor = view.FindControl<TextEditor>("Editor")!;
            Assert.False(view.FindControl<TextEditor>("AfterEditor")!.IsVisible);
            Assert.Equal(3, Grid.GetColumnSpan(editor));
            Assert.False(editor.TextArea.LeftMargins[0].IsVisible);
            editor.SelectAll();
            editor.Copy();
            Assert.Equal(raw, await window.Clipboard!.TryGetTextAsync());
        }
        finally { view.Clear(); window.Close(); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task OversizedAndMissingFilesShowLocalizedErrors()
    {
        var root = Directory.CreateTempSubdirectory("FKFinder_DiffErrors_");
        using var theme = new FastListTestTheme();
        try
        {
            await using var fixture = await TabCacheFixture.CreateAsync(entries: 0,
                configureServices: services => services.AddSingleton<IFileService>(new FakeFileService(root.FullName)));
            var entry = new FileSystemEntry { Name = "large.diff", FullPath = Path.Combine(root.FullName, "large.diff") };
            await using (var stream = File.Create(entry.FullPath)) stream.SetLength(TextPreviewReader.MaxBytes + 1);
            var preview = new SuperPreviewView(isCompactPreview: true);
            var window = CreateWindow(preview);
            try
            {
                window.Show();
                await preview.OpenAsync(entry);
                Assert.Equal(LocalizationText.Get("preview.text-too-large"), preview.FindControl<TextBlock>("PreviewPlaceholder")!.Text);
                File.Delete(entry.FullPath);
                await preview.OpenAsync(entry);
                Assert.Equal(LocalizationText.Get("preview.text-unreadable"), preview.FindControl<TextBlock>("PreviewPlaceholder")!.Text);
                Assert.Empty(preview.GetVisualDescendants().OfType<DiffPreviewView>());
            }
            finally { preview.Close(); window.Close(); }
        }
        finally { root.Delete(true); }
    }

    private static Window CreateWindow(Control content)
    {
        var window = new Window { Width = 800, Height = 550, Content = content };
        foreach (var asset in new[] { "ThemeTokens", "Styles", "ComponentStyles" })
            window.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri($"avares://MacExplorer/Assets/{asset}.axaml")));
        return window;
    }
}
