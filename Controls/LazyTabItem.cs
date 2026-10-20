using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace MacExplorer.Controls;

public class LazyTabItem : TabItem
{
    private INameScope? _contentScope;
    public string PageKey { get; set; } = "";
    public IControlTemplate? PageTemplate { get; set; }
    public bool IsContentLoaded => _contentScope != null;
    protected override Type StyleKeyOverride => typeof(TabItem);

    public void EnsureContent()
    {
        if (IsContentLoaded) return;
        var built = PageTemplate?.Build(this)
            ?? throw new InvalidOperationException($"Missing content template for {PageKey}.");
        _contentScope = built.NameScope;
        Content = built.Result;
    }

    public T? FindContentControl<T>(string name) where T : Control => _contentScope?.Find<T>(name);
}
