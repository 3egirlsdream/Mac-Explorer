using SharpCompress.Common;

namespace MacExplorer.Services.Impl;

internal static class ArchivePasswordAccess
{
    internal static bool IsPasswordError(Exception error) => error is CryptographicException
        || error is InvalidFormatException && error.Message.Contains("password", StringComparison.OrdinalIgnoreCase);

    internal static async Task<T> ReadAsync<T>(IArchiveService service, string path, Func<string?, Task<T>> read,
        Func<Task<string?>>? prompt, CancellationToken token = default, bool browse = false, string? sessionPassword = null,
        Action<string>? rememberSession = null, bool checkEncryption = false)
    {
        token.ThrowIfCancellationRequested();
        var password = sessionPassword ?? await service.ReadSavedPasswordAsync(path, token);
        try
        {
            if (password != null || !checkEncryption || !await Task.Run(() => service.IsEncrypted(path), token))
                return await read(password);
        }
        catch (Exception ex) when (prompt != null && IsPasswordError(ex)) { }

        token.ThrowIfCancellationRequested();
        if (prompt == null || string.IsNullOrEmpty(password = await prompt())) throw new OperationCanceledException(token);
        token.ThrowIfCancellationRequested();
        var result = await read(password);
        if (browse) await service.VerifyBrowsingPasswordAsync(path, password, token);
        token.ThrowIfCancellationRequested();
        rememberSession?.Invoke(password);
        return result;
    }
}
