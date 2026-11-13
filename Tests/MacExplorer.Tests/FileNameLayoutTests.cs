using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using MacExplorer.Controls;
using MacExplorer.Models;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Smoke", "UI")]
[Trait("Area", "FileList")]
public sealed class FileNameLayoutTests
{
    [AvaloniaTheory]
    [InlineData(136, 12, 2)]
    [InlineData(184, 12, 2)]
    [InlineData(184, 18, 2)]
    [InlineData(328, 15, 2)]
    [InlineData(170, 13, 1)]
    public void LongNamesKeepTheirExtensionInsideTheVisibleLines(double width, double size, int lines)
    {
        foreach (var name in new[]
        {
            "MacExplorer-2.0.3-vs-Finder-性能对比最终版.pptx",
            "一个很长的中文文件名称用于检查扩展名显示.csproj",
            "a-very-long-name-with-a-long-extension.drawingml",
            "family-👨‍👩‍👧‍👦-café-very-long-file-name.txt"
        })
        {
            var displayed = "";
            using var layout = FileNameText.CreateLayout(name, value =>
            {
                displayed = value;
                return new TextLayout(value, Typeface.Default, size, Brushes.Black,
                    textWrapping: lines == 1 ? TextWrapping.NoWrap : TextWrapping.Wrap,
                    maxWidth: width, maxLines: lines);
            });
            if (displayed != name) Assert.Contains("…", displayed);
            Assert.EndsWith(Path.GetExtension(name), displayed);
            Assert.StartsWith(name[..1], displayed);
            Assert.InRange(layout.TextLines.Count, 1, lines);
            Assert.Equal(displayed.Length, layout.TextLines.Sum(line => line.Length));
            Assert.InRange(layout.WidthIncludingTrailingWhitespace, 0, width + 0.01);
            Assert.All(layout.TextLines, line => Assert.False(line.HasCollapsed));
        }
    }

    [AvaloniaFact]
    public void LabelsRecalculateWhenWidthChangesAndKeepTheOriginalName()
    {
        const string name = "MacExplorer-2.0.3-vs-Finder-最终版.pptx";
        var label = new FileNameText { Text = name, FontSize = 12 };
        foreach (var width in new[] { 92d, 500d, 68d })
        {
            label.Measure(new Size(width, 40));
            label.Arrange(new Rect(0, 0, width, 40));
            var visible = VisibleText(label.TextLayout);
            Assert.Equal(name, label.Text);
            Assert.EndsWith(".pptx", visible);
            if (width == 500) Assert.Equal(name, visible);
            else Assert.Contains("…", visible);
        }
    }

    [AvaloniaTheory]
    [InlineData(false, 56)]
    [InlineData(true, 32)]
    [InlineData(true, 56)]
    [InlineData(true, 96)]
    [InlineData(true, 128)]
    public void FileListRendersTheExtensionAtEveryIconSize(bool grid, int iconSize)
    {
        var list = new FastFileList { IsGrid = grid, GridIconSize = iconSize };
        const string name = "MacExplorer-2.0.3-vs-Finder-性能对比最终版.pptx";
        list.SetRows([new FileSystemEntry { Name = name, FullPath = "/filename-test/" + name }]);
        list.Measure(new Size(600, 300));
        list.Arrange(new Rect(0, 0, 600, 300));
        var row = typeof(FastFileList).GetMethod("Texts", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!.Invoke(list, [list.Rows[0], 0])!;
        var cached = row.GetType().GetProperty("Name")!.GetValue(row)!;
        var layout = (TextLayout)cached.GetType().GetFields(System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic).Single(field => field.FieldType == typeof(TextLayout)).GetValue(cached)!;
        var visible = VisibleText(layout);
        Assert.Contains("…", visible);
        Assert.Contains(".pptx", visible);
        Assert.True(list.NameBounds(0).Bottom <= list.RowBounds(0).Bottom);
    }

    private static string VisibleText(TextLayout layout)
        => string.Concat(layout.TextLines.SelectMany(line => line.TextRuns)
            .OfType<ShapedTextRun>().Select(run => run.Text.ToString()));
}
