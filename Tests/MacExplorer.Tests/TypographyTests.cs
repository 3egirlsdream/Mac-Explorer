using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed class TypographyTests
{
    [AvaloniaFact]
    public void PresetsAlwaysScaleFromTheStandardTokensAndPersist()
    {
        var settings = new MemorySettingsService();
        var service = new TypographyService(settings);
        var changes = new List<FontSizePreset>();
        service.TypographyChanged += (_, args) => changes.Add(args.Preset);

        service.Initialize();
        Assert.Equal(FontSizePreset.Standard, service.CurrentPreset);
        AssertResource("FontSizeBody", 13);
        AssertResource("TypographyListRowMinHeight", 28);

        service.SetPreset(FontSizePreset.Large);
        AssertResource("FontSizeBody", 15);
        AssertResource("FontSizeLabel", 14);
        AssertResource("LineHeightBody", 23);
        AssertResource("TypographyListRowMinHeight", 32);

        service.SetPreset(FontSizePreset.Small);
        AssertResource("FontSizeBody", 11.5);
        AssertResource("FontSizeLabel", 11);
        AssertResource("LineHeightBody", 18);
        AssertResource("TypographyListRowMinHeight", 28);

        Assert.Equal("small", settings.Get(TypographyService.SettingKey));
        Assert.Equal([FontSizePreset.Large, FontSizePreset.Small], changes);
    }

    [AvaloniaFact]
    public void StoredPresetIsRestoredAndInvalidValueFallsBackToStandard()
    {
        var restored = new TypographyService(new MemorySettingsService
        {
            [TypographyService.SettingKey] = "large"
        });

        restored.Initialize();

        Assert.Equal(FontSizePreset.Large, restored.CurrentPreset);
        AssertResource("FontSizeBody", 15);

        var invalid = new TypographyService(new MemorySettingsService
        {
            [TypographyService.SettingKey] = "extra-large"
        });

        Assert.Equal(FontSizePreset.Standard, invalid.CurrentPreset);
    }

    [AvaloniaFact]
    public void ExistingFileListGroupAndSidebarTextUpdateWithoutRecreation()
    {
        var service = new TypographyService(new MemorySettingsService());
        service.Initialize();

        var application = Assert.IsAssignableFrom<Application>(Application.Current);
        Assert.True(application.TryGetResource("FontFamilyUi", application.ActualThemeVariant, out var fontResource));
        Assert.StartsWith("System Font", Assert.IsType<FontFamily>(fontResource).Name);

        var fileList = new FileListView();
        var listTemplate = Assert.IsAssignableFrom<IDataTemplate>(fileList.Resources["ListEntryTemplate"]);
        var fileRow = Assert.IsAssignableFrom<Control>(listTemplate.Build(new FileSystemEntry()));
        fileRow.DataContext = new FileSystemEntry
        {
            FullPath = "/tmp/example.txt",
            Name = "example.txt",
            Extension = ".txt"
        };

        var groupTemplate = Assert.IsAssignableFrom<IDataTemplate>(fileList.Resources["GroupedListRowTemplate"]);
        var groupRow = Assert.IsAssignableFrom<Control>(groupTemplate.Build(new FileListPresentationRow()));
        groupRow.DataContext = new FileListPresentationRow
        {
            GroupName = "今天",
            GroupItemCount = 1
        };

        var sidebar = new FinderSidebarView();
        var remoteServers = sidebar.FindControl<ItemsControl>("RemoteServersList")!;
        var remoteTemplate = Assert.IsAssignableFrom<IDataTemplate>(remoteServers.ItemTemplate);
        var remoteRow = Assert.IsAssignableFrom<Control>(remoteTemplate.Build(new RemoteServerInfo()));
        remoteRow.DataContext = new RemoteServerInfo
        {
            Name = "43.138.192.42",
            Host = "43.138.192.42",
            IsConnected = false
        };
        var textBox = new TextBox { Text = "动态字号" };
        var host = new StackPanel
        {
            Children = { fileRow, groupRow, sidebar, remoteRow, textBox }
        };
        var window = new Window { Width = 900, Height = 700, Content = host };
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/Styles.axaml")));
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var fileName = fileRow.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Classes.Contains("entry-name-text"));
        var groupHeader = groupRow.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Classes.Contains("file-group-header"));
        var groupContainer = groupRow.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Classes.Contains("file-group-row"));
        var sidebarTitle = sidebar.GetVisualDescendants().OfType<TextBlock>()
            .First(text => text.Classes.Contains("sidebar-section-title"));
        var sidebarItem = sidebar.FindControl<Border>("VolumeItem")!;
        var sidebarItemText = sidebarItem.GetVisualDescendants().OfType<TextBlock>().Single();
        var remoteAddress = remoteRow.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Text == "43.138.192.42");
        var remoteStatus = remoteRow.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Text == "未连接");
        var remoteAction = remoteRow.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Classes.Contains("sidebar-item-action"));

        Assert.Equal(13, fileName.FontSize);
        Assert.StartsWith("System Font", fileName.FontFamily.Name);
        Assert.Equal(Color.Parse("#252830"), Assert.IsType<SolidColorBrush>(fileName.Foreground).Color);
        Assert.Equal(12, groupHeader.FontSize);
        Assert.Equal(11, sidebarTitle.FontSize);
        Assert.Equal("PingFang SC", sidebarTitle.FontFamily.Name);
        Assert.Equal(FontWeight.Medium, sidebarTitle.FontWeight);
        Assert.Equal(FontWeight.Light, sidebarItemText.FontWeight);
        Assert.Equal(sidebarItemText.FontWeight, fileName.FontWeight);
        Assert.Equal(Color.Parse("#565D67"), Assert.IsType<SolidColorBrush>(sidebarTitle.Foreground).Color);
        Assert.Equal(14, remoteAddress.FontSize);
        Assert.Equal(13, remoteStatus.FontSize);
        Assert.Equal(0, remoteAction.Opacity);
        Assert.False(remoteAction.IsHitTestVisible);

        service.SetPreset(FontSizePreset.Large);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(15, fileName.FontSize);
        Assert.Equal(14, groupHeader.FontSize);
        Assert.Equal(12.5, sidebarTitle.FontSize);
        Assert.Equal(16, remoteAddress.FontSize);
        Assert.Equal(15, remoteStatus.FontSize);
        Assert.Equal(32, groupContainer.MinHeight);
        Assert.Equal(32, sidebarItem.MinHeight);
        Assert.Equal(39, textBox.MinHeight);

        window.Close();
    }

    private static void AssertResource(string key, double expected)
    {
        var application = Assert.IsAssignableFrom<Application>(Application.Current);
        Assert.True(application.TryGetResource(key, application.ActualThemeVariant, out var value));
        Assert.Equal(expected, Assert.IsType<double>(value));
    }

    private sealed class MemorySettingsService : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string this[string key]
        {
            set => _values[key] = value;
        }

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public T Get<T>(string key, T defaultValue)
        {
            var raw = Get(key);
            if (raw == null)
                return defaultValue;
            if (typeof(T) == typeof(string))
                return (T)(object)raw;
            if (typeof(T).IsEnum && Enum.TryParse(typeof(T), raw, true, out var parsed))
                return (T)parsed;
            return defaultValue;
        }

        public void Set(string key, string value) => _values[key] = value;

        public void Set<T>(string key, T value) => _values[key] = value?.ToString() ?? string.Empty;

        public Dictionary<string, string> GetAll() => new(_values, StringComparer.OrdinalIgnoreCase);
    }
}
