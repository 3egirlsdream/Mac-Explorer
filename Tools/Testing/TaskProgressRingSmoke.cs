#if DEBUG
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Tools.Testing;

/// <summary>Opt-in rendering/lifetime checks using synthetic tasks in an isolated native app.</summary>
internal static class TaskProgressRingSmoke
{
    public static async Task RunAsync(MainWindow owner)
    {
        var root = RuntimePaths.TestRoot ?? throw new InvalidOperationException("An isolated test profile is required.");
        var output = Path.Combine(root, "task-progress-ring");
        Directory.CreateDirectory(output);
        var checks = new List<object>();
        string? error = null;
        AppWindow? probe = null;
        try
        {
            var manager = App.Services.GetRequiredService<IBackgroundTaskManager>();
            var task = manager.AddTask("Progress ring rendering fixture", BackgroundTaskKind.Copy);
            var ring = owner.FindControl<AnimatedProgressRing>("TaskProgressRing")!;
            var button = owner.FindControl<Button>("TaskButton")!;
            await WaitAsync(() => button.IsVisible && ring.IsAnimationActive);
            foreach (var dark in new[] { false, true })
            {
                owner.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                foreach (var value in new[] { 0d, 25, 50, 100 })
                {
                    manager.UpdateProgress(task.Id, value, "");
                    await WaitAsync(() => ring.Value == value);
                    await Task.Delay(180);
                    await CaptureAsync(ring, $"{(dark ? "dark" : "light")}-{value:0}", 8);
                }
                manager.UpdateProgress(task.Id, 50, "");
                await WaitAsync(() => ring.Value == 50);
                await Task.Delay(180);
                owner.MinWidth = 0; owner.Width = 720;
                await CaptureAsync(owner, dark ? "window-dark" : "window-light", 1);
            }
            manager.UpdateProgress(task.Id, 62, "");
            await WaitAsync(() => ring.Value == 62);
            await Task.Delay(180);
            await CaptureAsync(ring, "flow-a", 8);
            var before = ring.AnimationFrameCount;
            await WaitAsync(() => ring.AnimationFrameCount >= before + 20);
            await CaptureAsync(ring, "flow-b", 8);
            checks.Add(new { scenario = "running", frames = ring.AnimationFrameCount - before });

            task.State = BackgroundTaskState.Paused;
            manager.UpdateProgress(task.Id, 62, "");
            await WaitAsync(() => !ring.IsAnimating);
            await AssertStoppedAsync(ring, "paused");
            await CaptureAsync(ring, "paused", 8);
            task.State = BackgroundTaskState.Running; task.IsIndeterminate = true;
            manager.UpdateProgress(task.Id, 62, "");
            await WaitAsync(() => ring.IsIndeterminate && ring.IsAnimating);
            await CaptureAsync(ring, "indeterminate", 8);
            if (ToolTip.GetTip(button)!.ToString()!.Contains('%')) throw new InvalidOperationException("Unknown progress reported a percentage.");

            button.IsVisible = false;
            await AssertStoppedAsync(ring, "ancestor-hidden");
            button.IsVisible = true;
            before = ring.AnimationFrameCount;
            await WaitAsync(() => ring.AnimationFrameCount > before);
            owner.WindowState = WindowState.Minimized;
            await AssertStoppedAsync(ring, "minimized");
            owner.WindowState = WindowState.Normal;
            owner.Show(); owner.Activate();
            before = ring.AnimationFrameCount;
            await WaitAsync(() => ring.AnimationFrameCount > before);
            owner.Hide();
            await AssertStoppedAsync(ring, "window-hidden");
            owner.Show();
            before = ring.AnimationFrameCount;
            await WaitAsync(() => ring.AnimationFrameCount > before);

            task.IsIndeterminate = false;
            manager.UpdateProgress(task.Id, 62, "");
            await WaitAsync(() => !ring.IsIndeterminate && ring.Value == 62);
            await Task.Delay(180);
            manager.CompleteTask(task.Id);
            await WaitAsync(() => !ring.IsAnimating && ring.Classes.Contains("completed"));
            await AssertStoppedAsync(ring, "completed");
            await CaptureAsync(ring, "completed", 8);

            var testRing = new AnimatedProgressRing { Value = 45, IsAnimating = true };
            var host = new Border { Child = testRing };
            probe = new AppWindow { Width = 240, Height = 160, Content = host };
            probe.Show();
            await WaitAsync(() => testRing.AnimationFrameCount > 2);
            host.Child = null;
            await WaitAsync(() => testRing.RenderResourcesReleased);
            await AssertStoppedAsync(testRing, "detached");
            host.Child = testRing;
            before = testRing.AnimationFrameCount;
            await WaitAsync(() => testRing.AnimationFrameCount > before + 2);
            probe.Close();
            await WaitAsync(() => testRing.RenderResourcesReleased);
            await AssertStoppedAsync(testRing, "closed");
            checks.Add(new { scenario = "render-resources-released", released = testRing.RenderResourcesReleased });
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            probe?.Close();
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"),
                JsonSerializer.Serialize(new { success = error == null, checks, error }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Task progress ring smoke: {output}; success={error == null}");
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
                lifetime.Shutdown(error == null ? 0 : 1);
        }

        async Task AssertStoppedAsync(AnimatedProgressRing ring, string scenario)
        {
            await CommitAsync(ring);
            await Task.Delay(100);
            var before = ring.AnimationFrameCount;
            await Task.Delay(180);
            var frames = ring.AnimationFrameCount - before;
            checks.Add(new { scenario, frames });
            if (frames != 0) throw new InvalidOperationException($"Animation continued while {scenario}: {frames} frames.");
        }

        async Task CaptureAsync(Visual visual, string name, double scale)
        {
            await CommitAsync(visual);
            var composition = (visual is AnimatedProgressRing
                ? ElementComposition.GetElementChildVisual(visual)
                : ElementComposition.GetElementVisual(visual))!;
            using var bitmap = await composition.Compositor.CreateCompositionVisualSnapshot(composition, scale);
            bitmap.Save(Path.Combine(output, name + ".png"));
        }
    }

    private static async Task CommitAsync(Visual visual)
    {
        if (visual is Control control) control.UpdateLayout();
        if (ElementComposition.GetElementVisual(visual) is { } composition)
            await composition.Compositor.RequestCommitAsync();
    }

    private static async Task WaitAsync(Func<bool> ready)
    {
        var timer = Stopwatch.StartNew();
        while (!ready())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Progress ring did not reach its expected state.");
            await Task.Delay(20);
        }
    }
}
#endif
