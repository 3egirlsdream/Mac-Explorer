using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MacExplorer.Controls;

public sealed class RenameDiffText : TextBlock
{
    public static readonly StyledProperty<string> BeforeProperty = AvaloniaProperty.Register<RenameDiffText, string>(nameof(Before), "");
    public static readonly StyledProperty<string> AfterProperty = AvaloniaProperty.Register<RenameDiffText, string>(nameof(After), "");
    public string Before { get => GetValue(BeforeProperty); set => SetValue(BeforeProperty, value); }
    public string After { get => GetValue(AfterProperty); set => SetValue(AfterProperty, value); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != BeforeProperty && change.Property != AfterProperty) return;
        var before = Before ?? ""; var after = After ?? "";
        var prefix = 0; var suffix = 0;
        while (prefix < Math.Min(before.Length, after.Length) && before[prefix] == after[prefix]) prefix++;
        while (suffix < Math.Min(before.Length, after.Length) - prefix && before[^(suffix + 1)] == after[^(suffix + 1)]) suffix++;
        // Keep combining characters and joined Emoji in one text run.
        var beforeBoundaries = StringInfo.ParseCombiningCharacters(before);
        var afterBoundaries = StringInfo.ParseCombiningCharacters(after);
        static bool IsBoundary(int[] boundaries, int position, int length) =>
            position == length || Array.BinarySearch(boundaries, position) >= 0;
        while (prefix > 0 && (!IsBoundary(beforeBoundaries, prefix, before.Length)
            || !IsBoundary(afterBoundaries, prefix, after.Length))) prefix--;
        while (suffix > 0 && (!IsBoundary(beforeBoundaries, before.Length - suffix, before.Length)
            || !IsBoundary(afterBoundaries, after.Length - suffix, after.Length))) suffix--;
        Inlines?.Clear();
        Inlines?.Add(new Run(after[..prefix]));
        var difference = new Run(after.Substring(prefix, after.Length - prefix - suffix)) { FontWeight = FontWeight.SemiBold };
        difference.Bind(TextElement.ForegroundProperty, this.GetResourceObservable("AccentBrush"));
        Inlines?.Add(difference);
        Inlines?.Add(new Run(suffix > 0 ? after[^suffix..] : ""));
    }
}
