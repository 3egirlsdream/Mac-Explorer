using MacExplorer.Services;

namespace MacExplorer.Services.Impl;

internal enum FileListColumn
{
    Name,
    Modified,
    Size,
    Type
}

internal readonly record struct FileListColumnWidths(
    double Name,
    double Modified,
    double Size,
    double Type)
{
    public double Total => Name + Modified + Size + Type;

    public double this[FileListColumn column] => column switch
    {
        FileListColumn.Name => Name,
        FileListColumn.Modified => Modified,
        FileListColumn.Size => Size,
        FileListColumn.Type => Type,
        _ => throw new ArgumentOutOfRangeException(nameof(column))
    };

    public FileListColumnWidths With(FileListColumn column, double value) => column switch
    {
        FileListColumn.Name => this with { Name = value },
        FileListColumn.Modified => this with { Modified = value },
        FileListColumn.Size => this with { Size = value },
        FileListColumn.Type => this with { Type = value },
        _ => throw new ArgumentOutOfRangeException(nameof(column))
    };
}

internal sealed class FileListColumnLayoutService
{
    internal const string NameWidthKey = "file_list.column.name_width";
    internal const string ModifiedWidthKey = "file_list.column.modified_width";
    internal const string SizeWidthKey = "file_list.column.size_width";
    internal const string TypeWidthKey = "file_list.column.type_width";

    internal static readonly FileListColumnWidths Defaults = new(420, 170, 110, 110);
    internal static readonly FileListColumnWidths Minimums = new(220, 140, 80, 80);
    internal static readonly FileListColumnWidths Maximums = new(720, 240, 160, 180);

    private readonly ISettingsService? _settings;

    public FileListColumnLayoutService(ISettingsService? settings = null)
    {
        _settings = settings;
        PreferredWidths = Clamp(new FileListColumnWidths(
            settings?.Get(NameWidthKey, Defaults.Name) ?? Defaults.Name,
            settings?.Get(ModifiedWidthKey, Defaults.Modified) ?? Defaults.Modified,
            settings?.Get(SizeWidthKey, Defaults.Size) ?? Defaults.Size,
            settings?.Get(TypeWidthKey, Defaults.Type) ?? Defaults.Type));
    }

    public event EventHandler? PreferredWidthsChanged;

    public FileListColumnWidths PreferredWidths { get; private set; }

    public void Preview(FileListColumn column, double width)
    {
        var next = Clamp(PreferredWidths.With(column, width));
        if (next == PreferredWidths)
            return;

        PreferredWidths = next;
        PreferredWidthsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Commit(FileListColumn column)
    {
        if (_settings == null)
            return;

        var key = GetSettingKey(column);
        _settings.Set(key, PreferredWidths[column]);
    }

    public void Reset(FileListColumn column)
    {
        Preview(column, Defaults[column]);
        Commit(column);
    }

    internal static FileListColumnWidths CalculateEffective(
        FileListColumnWidths preferred,
        double availableWidth)
    {
        var effective = Clamp(preferred);
        if (!double.IsFinite(availableWidth) || availableWidth <= 0)
            return effective;

        // The default name column fills the window; an explicitly resized name
        // keeps its preferred width. Double-clicking its divider restores fill.
        if (effective.Total <= availableWidth)
            return preferred.Name == Defaults.Name
                ? effective.With(FileListColumn.Name, effective.Name + availableWidth - effective.Total)
                : effective;

        var overflow = effective.Total - availableWidth;
        effective = Shrink(effective, FileListColumn.Name, Minimums.Name, ref overflow);
        effective = Shrink(effective, FileListColumn.Type, Minimums.Type, ref overflow);
        effective = Shrink(effective, FileListColumn.Size, Minimums.Size, ref overflow);
        effective = Shrink(effective, FileListColumn.Modified, Minimums.Modified, ref overflow);
        return effective;
    }

    internal static double ClampInteractiveWidth(
        FileListColumn column,
        double requestedWidth,
        FileListColumnWidths effective,
        double availableWidth)
    {
        var minimum = Minimums[column];
        var maximum = Maximums[column];
        if (double.IsFinite(availableWidth) && availableWidth > 0)
        {
            var otherColumns = effective.Total - effective[column];
            if (column != FileListColumn.Name)
                otherColumns -= Math.Max(0, effective.Name - Minimums.Name);
            maximum = Math.Min(maximum, Math.Max(minimum, availableWidth - otherColumns));
        }

        return Math.Clamp(requestedWidth, minimum, maximum);
    }

    private static FileListColumnWidths Clamp(FileListColumnWidths widths) => new(
        Math.Clamp(widths.Name, Minimums.Name, Maximums.Name),
        Math.Clamp(widths.Modified, Minimums.Modified, Maximums.Modified),
        Math.Clamp(widths.Size, Minimums.Size, Maximums.Size),
        Math.Clamp(widths.Type, Minimums.Type, Maximums.Type));

    private static FileListColumnWidths Shrink(
        FileListColumnWidths widths,
        FileListColumn column,
        double minimum,
        ref double overflow)
    {
        if (overflow <= 0)
            return widths;

        var current = widths[column];
        var reduction = Math.Min(current - minimum, overflow);
        overflow -= reduction;
        return widths.With(column, current - reduction);
    }

    private static string GetSettingKey(FileListColumn column) => column switch
    {
        FileListColumn.Name => NameWidthKey,
        FileListColumn.Modified => ModifiedWidthKey,
        FileListColumn.Size => SizeWidthKey,
        FileListColumn.Type => TypeWidthKey,
        _ => throw new ArgumentOutOfRangeException(nameof(column))
    };
}
