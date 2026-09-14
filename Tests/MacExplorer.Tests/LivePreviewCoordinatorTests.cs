using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed class LivePreviewCoordinatorTests
{
    [Fact]
    public async Task SwitchingWaitsForOldWorkspaceReleaseBeforeStartingNewWorkspace()
    {
        var events = new List<string>();
        var releaseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new FakeWorkspace("A", events)
        {
            DisableStarted = releaseStarted,
            DisableGate = allowRelease.Task
        };
        var second = new FakeWorkspace("B", events);
        var coordinator = new LivePreviewCoordinator(workspace => workspace.CanActivateLivePreview);

        await coordinator.ActivateAsync(first);
        var switching = coordinator.ActivateAsync(second);
        await releaseStarted.Task;

        Assert.DoesNotContain("B:on", events);
        allowRelease.SetResult();
        await switching;

        Assert.True(events.IndexOf("A:off:end") < events.IndexOf("B:on"));
        Assert.Same(second, coordinator.Current);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task StaleActivationCannotBecomeCurrentAfterANewerRequest()
    {
        var events = new List<string>();
        var enableStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowEnable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new FakeWorkspace("A", events)
        {
            EnableStarted = enableStarted,
            EnableGate = allowEnable.Task
        };
        var second = new FakeWorkspace("B", events);
        var coordinator = new LivePreviewCoordinator(workspace => workspace.CanActivateLivePreview);

        var firstActivation = coordinator.ActivateAsync(first);
        await enableStarted.Task;
        var secondActivation = coordinator.ActivateAsync(second);
        allowEnable.SetResult();
        await Task.WhenAll(firstActivation, secondActivation);

        Assert.Contains("A:off:end", events);
        Assert.Equal(1, second.EnabledCount);
        Assert.Same(second, coordinator.Current);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task RepeatingTheAlreadyActiveWorkspaceDoesNotReloadItsPreview()
    {
        var workspace = new FakeWorkspace("A", []);
        var coordinator = new LivePreviewCoordinator(candidate => candidate.CanActivateLivePreview);

        await coordinator.ActivateAsync(workspace);
        var generation = coordinator.ActivationGeneration;
        await coordinator.ActivateAsync(workspace);

        Assert.Equal(1, workspace.EnabledCount);
        Assert.Equal(generation, coordinator.ActivationGeneration);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task EvictingUnrelatedWorkspaceDoesNotCancelPendingActivation()
    {
        var events = new List<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selected = new FakeWorkspace("selected", events) { EnableStarted = started, EnableGate = release.Task };
        var evicted = new FakeWorkspace("evicted", events);
        await using var coordinator = new LivePreviewCoordinator(workspace => workspace.CanActivateLivePreview);
        var activation = coordinator.ActivateAsync(selected);
        await started.Task;
        var cleanup = coordinator.DeactivateAndReleaseAsync(evicted);
        Assert.Same(activation, coordinator.ActivateAsync(selected));
        release.SetResult();
        await Task.WhenAll(activation, cleanup);
        Assert.Same(selected, coordinator.Current);
        Assert.Equal(1, selected.EnabledCount);
        Assert.DoesNotContain("selected:off:start", events);
    }

    [Fact]
    public async Task ClosingWorkspaceDuringActivationPreventsItBecomingCurrent()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closing = new FakeWorkspace("closing", []) { EnableStarted = started, EnableGate = release.Task };
        await using var coordinator = new LivePreviewCoordinator(workspace => workspace.CanActivateLivePreview);
        var activation = coordinator.ActivateAsync(closing);
        await started.Task;
        closing.CanActivateLivePreview = false;
        var cleanup = coordinator.DeactivateAndReleaseAsync(closing);
        release.SetResult();
        await Task.WhenAll(activation, cleanup);
        Assert.Null(coordinator.Current);
    }

    private sealed class FakeWorkspace(string name, List<string> events) : ILivePreviewWorkspace
    {
        public bool CanActivateLivePreview { get; set; } = true;
        public int EnabledCount { get; private set; }
        public TaskCompletionSource? EnableStarted { get; init; }
        public TaskCompletionSource? DisableStarted { get; init; }
        public Task? EnableGate { get; init; }
        public Task? DisableGate { get; init; }

        public async Task SetLivePreviewStateAsync(bool enabled, long activationGeneration)
        {
            if (enabled)
            {
                EnabledCount++;
                events.Add($"{name}:on");
                EnableStarted?.TrySetResult();
                if (EnableGate != null)
                    await EnableGate;
                return;
            }

            events.Add($"{name}:off:start");
            DisableStarted?.TrySetResult();
            if (DisableGate != null)
                await DisableGate;
            events.Add($"{name}:off:end");
        }
    }
}
