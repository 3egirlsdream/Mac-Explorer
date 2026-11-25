using MacExplorer.Services;

namespace MacExplorer.Services.Impl;

internal enum FileListColumn
{
    Name,
    Modified,
    Size,
    Type
}

[Flags]
internal enum FileListColumns
{
    None = 0,
    Name = 1 << FileListColumn.Name,
    Modified = 1 << FileListColumn.Modified,
    Size = 1 << FileListColumn.Size,
    Type = 1 << FileListColumn.Type,
    Metadata = Modified | Size | Type,
    All = Name | Metadata
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
    private const string HiddenColumnsKey = "file_list.column.hidden";
    private const string ResizedColumnsKey = "file_list.column.resized";
    internal static readonly IReadOnlyList<FileListColumn> Columns = Enum.GetValues<FileListColumn>();

    internal static readonly FileListColumnWidths Defaults = new(420, 170, 110, 230);
    internal static readonly FileListColumnWidths Minimums = new(220, 140, 80, 80);
    internal static readonly FileListColumnWidths Maximums = new(8192, 240, 160, 360);

    private readonly ISettingsService? _settings;

    public FileListColumnLayoutService(ISettingsService? settings = null)
    {
        _settings = settings;
        PreferredWidths = Clamp(new FileListColumnWidths(
            settings?.Get(NameWidthKey, Defaults.Name) ?? Defaults.Name,
            settings?.Get(ModifiedWidthKey, Defaults.Modified) ?? Defaults.Modified,
            settings?.Get(SizeWidthKey, Defaults.Size) ?? Defaults.Size,
            settings?.Get(TypeWidthKey, Defaults.Type) ?? Defaults.Type));
        HiddenColumns = (FileListColumns)(settings?.Get(HiddenColumnsKey, 0) ?? 0) & FileListColumns.Metadata;
        var resized = settings?.Get(ResizedColumnsKey, -1) ?? -1;
        ResizedColumns = resized < 0 ? InferResizedColumns(PreferredWidths) : (FileListColumns)resized & FileListColumns.All;
    }

    public event EventHandler? LayoutChanged;
    internal event Action? Committed;
    internal bool DirectoryScoped { get; set; }
    public FileListColumnWidths PreferredWidths { get; private set; }
    public FileListColumns HiddenColumns { get; private set; }
    public FileListColumns ResizedColumns { get; private set; }
    public FileListColumns? ResizedColumnsOverride
        => ResizedColumns == InferResizedColumns(PreferredWidths) ? null : ResizedColumns;

    internal static FileListColumns Mask(FileListColumn column) => (FileListColumns)(1 << (int)column);

    internal void Restore(double[] widths, FileListColumns hidden, FileListColumns? resized)
    {
        if (widths.Length == 4 && widths.All(double.IsFinite))
            PreferredWidths = Clamp(new(widths[0], widths[1], widths[2], widths[3]));
        HiddenColumns = hidden & FileListColumns.Metadata;
        ResizedColumns = (resized ?? InferResizedColumns(PreferredWidths)) & FileListColumns.All;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Preview(FileListColumn column, double width)
    {
        var next = PreferredWidths.With(column, ClampWidth(column, width));
        var resized = ResizedColumns | Mask(column);
        if (next == PreferredWidths && resized == ResizedColumns)
            return;

        PreferredWidths = next;
        ResizedColumns = resized;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Commit(FileListColumn column)
    {
        if (DirectoryScoped)
        {
            Committed?.Invoke();
            return;
        }

        _settings?.Set(GetSettingKey(column), PreferredWidths[column]);
        _settings?.Set(ResizedColumnsKey, (int)ResizedColumns);
    }

    public void Reset(FileListColumn column)
    {
        PreferredWidths = PreferredWidths.With(column, Defaults[column]);
        ResizedColumns &= ~Mask(column);
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        Commit(column);
    }

    public void SetVisible(FileListColumn column, bool visible)
    {
        var mask = Mask(column) & FileListColumns.Metadata;
        var hidden = visible ? HiddenColumns & ~mask : HiddenColumns | mask;
        if (hidden == HiddenColumns)
            return;

        HiddenColumns = hidden;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        if (DirectoryScoped)
            Committed?.Invoke();
        else
            _settings?.Set(HiddenColumnsKey, (int)HiddenColumns);
    }

    internal static FileListColumnWidths CalculateEffective(
        FileListColumnWidths preferred, double availableWidth,
        FileListColumns hidden = FileListColumns.None, FileListColumns? resized = null)
    {
        hidden &= FileListColumns.Metadata;
        var effective = Defaults;
        foreach (var column in Columns)
            if ((hidden & Mask(column)) != 0)
                effective = effective.With(column, 0);

        var fixedColumns = (resized ?? InferResizedColumns(preferred)) & ~hidden;
        if (double.IsFinite(availableWidth) && availableWidth > 0)
        {
            // Fit the default layout first. Applying manual widths afterwards keeps
            // neighbouring dividers from moving in response to a drag.
            var overflow = effective.Total - availableWidth;
            if (overflow <= 0)
                effective = effective.With(FileListColumn.Name, effective.Name - overflow);
            else
            {
                effective = Shrink(effective, FileListColumn.Name, ref overflow);
                effective = Shrink(effective, FileListColumn.Type, ref overflow);
                effective = Shrink(effective, FileListColumn.Size, ref overflow);
                effective = Shrink(effective, FileListColumn.Modified, ref overflow);
            }
        }

        foreach (var column in Columns)
            if ((fixedColumns & Mask(column)) != 0)
                effective = effective.With(column, ClampWidth(column, preferred[column]));
        return effective;
    }

    internal static double ClampWidth(FileListColumn column, double width)
        => double.IsFinite(width) ? Math.Clamp(width, Minimums[column], Maximums[column]) : Defaults[column];

    private static FileListColumnWidths Clamp(FileListColumnWidths widths) => new(
        ClampWidth(FileListColumn.Name, widths.Name),
        ClampWidth(FileListColumn.Modified, widths.Modified),
        ClampWidth(FileListColumn.Size, widths.Size),
        ClampWidth(FileListColumn.Type, widths.Type));

    private static FileListColumns InferResizedColumns(FileListColumnWidths widths)
    {
        var resized = FileListColumns.None;
        foreach (var column in Columns)
            if (widths[column] != Defaults[column])
                resized |= Mask(column);
        return resized;
    }

    private static FileListColumnWidths Shrink(FileListColumnWidths widths, FileListColumn column, ref double overflow)
    {
        var current = widths[column];
        if (current == 0 || overflow <= 0)
            return widths;

        var reduction = Math.Min(current - Minimums[column], overflow);
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
