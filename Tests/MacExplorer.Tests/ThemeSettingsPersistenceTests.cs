using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "Appearance")]
public sealed class ThemeSettingsPersistenceTests
{
    [AvaloniaFact(Explicit = true)]
    public async Task ThemeChangesApplyWhileDatabaseIsLockedAndFlushLatestValues()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("theme-settings-");
        var factory = new DatabaseConnectionFactory(Path.Combine(root.FullName, "settings.db"));
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unlocked = false;
        try
        {
            using var connection = factory.GetConnection();
            using (var schema = connection.CreateCommand())
            {
                schema.CommandText = "CREATE TABLE app_settings(key TEXT PRIMARY KEY, value TEXT)";
                schema.ExecuteNonQuery();
            }
            using var settings = new SettingsService(factory);
            var writer = Task.Run(async () =>
            {
                using var blocker = factory.GetConnection();
                using var transaction = blocker.BeginTransaction();
                acquired.SetResult();
                try { await release.Task.WaitAsync(TimeSpan.FromSeconds(5), ct); }
                finally
                {
                    transaction.Commit();
                    Volatile.Write(ref unlocked, true);
                }
            });
            try
            {
                await acquired.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                var interaction = new InteractionStyleService(settings);
                interaction.Initialize();
                var typography = new TypographyService(settings);
                typography.Initialize();
                var theme = new MacThemeService(settings);
                theme.SetThemeMode("dark");
                typography.SetPreset(FontSizePreset.Large);
                interaction.SetCornerRadius(10);
                interaction.SetDisabledOpacity(0.5);
                Assert.True(interaction.TrySetColor(InteractionStyleToken.Hover, InteractionThemeVariant.Dark, "#123456"));
                interaction.ResetColors(InteractionThemeVariant.Dark);
                Assert.True(interaction.TrySetColor(InteractionStyleToken.Hover, InteractionThemeVariant.Dark, "#654321"));

                Assert.False(Volatile.Read(ref unlocked), "Theme changes waited for the SQLite write lock.");
                Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
                Assert.Equal(FontSizePreset.Large, typography.CurrentPreset);
                Assert.Equal("dark", settings.Get("theme_mode"));
                Assert.Equal("large", settings.Get("font_size_preset"));
                Assert.Equal("#654321", interaction.GetColor(InteractionStyleToken.Hover, InteractionThemeVariant.Dark));
                Assert.True(Application.Current.TryGetResource("InteractionHoverBrush", ThemeVariant.Dark, out var brush));
                Assert.Equal(Color.Parse("#654321"), Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color);
            }
            finally
            {
                release.TrySetResult();
                await writer;
            }
            settings.Dispose();
            using var restored = new SettingsService(factory);
            Assert.Equal("dark", restored.Get("theme_mode"));
            Assert.Equal("large", restored.Get("font_size_preset"));
            Assert.Equal("#654321", restored.Get("interaction.hover.dark"));
            Assert.Equal("", restored.Get("interaction.selected.dark"));
            Assert.Equal("10", restored.Get("interaction.corner_radius"));
            Assert.Equal(0.5, double.Parse(restored.Get("interaction.disabled_opacity")!, System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(true);
        }
    }
}
