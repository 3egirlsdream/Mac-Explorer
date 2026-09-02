using System;
using System.Threading;
using System.Threading.Tasks;

namespace MacExplorer.Views;

internal interface ILivePreviewWorkspace
{
    bool CanActivateLivePreview { get; }
    Task SetLivePreviewStateAsync(bool enabled, long activationGeneration);
}

internal sealed class LivePreviewCoordinator : IAsyncDisposable
{
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private readonly Func<ILivePreviewWorkspace, bool> _isStillActive;
    private ILivePreviewWorkspace? _current;
    private long _activationGeneration;
    private bool _disposed;

    internal LivePreviewCoordinator(Func<ILivePreviewWorkspace, bool> isStillActive)
    {
        _isStillActive = isStillActive;
    }

    internal long ActivationGeneration => Volatile.Read(ref _activationGeneration);
    internal ILivePreviewWorkspace? Current => _current;

    public Task ActivateAsync(ILivePreviewWorkspace? nextWorkspace)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReferenceEquals(_current, nextWorkspace)
            && nextWorkspace?.CanActivateLivePreview == true
            && _isStillActive(nextWorkspace))
            return Task.CompletedTask;
        var generation = Interlocked.Increment(ref _activationGeneration);
        return ActivateCoreAsync(nextWorkspace, generation);
    }

    private async Task ActivateCoreAsync(ILivePreviewWorkspace? nextWorkspace, long generation)
    {
        await _switchGate.WaitAsync();
        try
        {
            if (_current != null)
            {
                var oldWorkspace = _current;
                _current = null;
                await oldWorkspace.SetLivePreviewStateAsync(false, generation);
            }

            if (generation != ActivationGeneration
                || nextWorkspace == null
                || !nextWorkspace.CanActivateLivePreview
                || !_isStillActive(nextWorkspace))
                return;

            await nextWorkspace.SetLivePreviewStateAsync(true, generation);
            if (generation != ActivationGeneration
                || !nextWorkspace.CanActivateLivePreview
                || !_isStillActive(nextWorkspace))
            {
                await nextWorkspace.SetLivePreviewStateAsync(false, ActivationGeneration);
                return;
            }

            _current = nextWorkspace;
        }
        finally
        {
            _switchGate.Release();
        }
    }

    public Task DeactivateAndReleaseAsync(ILivePreviewWorkspace workspace)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var generation = Interlocked.Increment(ref _activationGeneration);
        return DeactivateAndReleaseCoreAsync(workspace, generation);
    }

    private async Task DeactivateAndReleaseCoreAsync(ILivePreviewWorkspace workspace, long generation)
    {
        await _switchGate.WaitAsync();
        try
        {
            if (ReferenceEquals(_current, workspace))
                _current = null;
            await workspace.SetLivePreviewStateAsync(false, generation);
        }
        finally
        {
            _switchGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        Interlocked.Increment(ref _activationGeneration);

        await _switchGate.WaitAsync();
        try
        {
            if (_current != null)
            {
                var workspace = _current;
                _current = null;
                await workspace.SetLivePreviewStateAsync(false, ActivationGeneration);
            }
        }
        finally
        {
            _switchGate.Release();
            _switchGate.Dispose();
        }
    }
}
