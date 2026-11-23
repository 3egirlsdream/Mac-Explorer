using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SharpCompress.Archives;

namespace MacExplorer.Services.Impl;

internal sealed class ArchivePasswordVault(ISettingsService settings, DatabaseConnectionFactory database,
    Func<bool, byte[]?> readKey)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string> _fingerprints = new(StringComparer.Ordinal);

    internal bool Enabled => settings.Get(ArchivePreferences.SavePasswords, false);

    internal sealed record Source(string[] Paths, string Version, long Size);

    internal Source? Capture(string archivePath)
    {
        if (!Enabled) return null;
        try
        {
            DirectoryAccess.Current.EnsureAccess(archivePath);
            var paths = ArchiveFactory.GetFileParts(archivePath).ToArray();
            if (paths.Length == 0) paths = [archivePath];
            long size = 0;
            var version = new StringBuilder();
            foreach (var path in paths)
            {
                DirectoryAccess.Current.EnsureAccess(path);
                var info = new FileInfo(path);
                var identity = FileIdentitySnapshot.Capture(path, false);
                if (!info.Exists || identity == null) return null;
                version.Append(identity.Identity).Append(':').Append(info.Length).Append(':')
                    .Append(info.LastWriteTimeUtc.Ticks).Append('|');
                size += info.Length;
            }
            return new(paths, version.ToString(), size);
        }
        catch (Exception ex) when (IsStorageFailure(ex) || ex is SharpCompress.Common.SharpCompressException) { return null; }
    }

    internal Task<string?> ReadAsync(string path, CancellationToken ct) => UseAsync(async () =>
    {
        var source = Capture(path);
        if (source == null) return null;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM archive_passwords WHERE size = $size LIMIT 1";
        command.Parameters.AddWithValue("$size", source.Size);
        if (command.ExecuteScalar() == null) return null;
        var fingerprint = await FingerprintAsync(path, source, ct).ConfigureAwait(false);
        if (fingerprint == null || !Enabled) return null;
        command.CommandText = "SELECT secret FROM archive_passwords WHERE fingerprint = $fingerprint AND size = $size";
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        if (command.ExecuteScalar() is not byte[] secret || secret.Length <= 28) return null;
        var key = readKey(false);
        if (key == null) return null;
        var plaintext = new byte[secret.Length - 28];
        try
        {
            using var cipher = new AesGcm(key, 16);
            cipher.Decrypt(secret.AsSpan(0, 12), secret.AsSpan(28), secret.AsSpan(12, 16), plaintext,
                Encoding.ASCII.GetBytes(fingerprint));
            return Enabled ? Encoding.UTF8.GetString(plaintext) : null;
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plaintext); }
    }, ct);

    internal Task SaveAsync(string path, Source? source, string password, CancellationToken ct)
    {
        if (source == null || !Enabled) return Task.CompletedTask;
        return UseAsync(async () =>
        {
            var fingerprint = await FingerprintAsync(path, source, ct).ConfigureAwait(false);
            if (fingerprint == null || !Enabled) return null;
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM archive_passwords LIMIT 1";
            var key = readKey(command.ExecuteScalar() == null);
            if (key == null) return null;
            var plaintext = Encoding.UTF8.GetBytes(password);
            try
            {
                var secret = new byte[28 + plaintext.Length];
                RandomNumberGenerator.Fill(secret.AsSpan(0, 12));
                using var cipher = new AesGcm(key, 16);
                cipher.Encrypt(secret.AsSpan(0, 12), plaintext, secret.AsSpan(28), secret.AsSpan(12, 16),
                    Encoding.ASCII.GetBytes(fingerprint));
                ct.ThrowIfCancellationRequested();
                if (!Enabled) return null;
                command.CommandText = """
                    INSERT INTO archive_passwords(fingerprint, size, secret) VALUES($fingerprint, $size, $secret)
                    ON CONFLICT(fingerprint) DO UPDATE SET secret = excluded.secret;
                    """;
                command.Parameters.AddWithValue("$fingerprint", fingerprint);
                command.Parameters.AddWithValue("$size", source.Size);
                command.Parameters.AddWithValue("$secret", secret);
                command.ExecuteNonQuery();
                return fingerprint;
            }
            finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plaintext); }
        }, ct);
    }

    private Task<string?> UseAsync(Func<Task<string?>> operation, CancellationToken ct)
        => !Enabled ? Task.FromResult<string?>(null) : Task.Run(async () =>
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try { return Enabled ? await operation().ConfigureAwait(false) : null; }
            catch (Exception ex) when (IsStorageFailure(ex)) { return null; }
            finally { _gate.Release(); }
        }, ct);

    private async Task<string?> FingerprintAsync(string path, Source source, CancellationToken ct)
    {
        if (Capture(path)?.Version != source.Version) return null;
        if (_fingerprints.TryGetValue(source.Version, out var existing)) return existing;
        string fingerprint;
        if (source.Paths.Length == 1)
        {
            fingerprint = (await FileHashCalculator.ComputeAsync(source.Paths[0], ct).ConfigureAwait(false))!;
        }
        else
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            digest.AppendData("MacExplorer archive volumes\0"u8);
            var count = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(count, source.Paths.Length);
            digest.AppendData(count);
            foreach (var volume in source.Paths)
                digest.AppendData(Convert.FromHexString((await FileHashCalculator.ComputeAsync(volume, ct).ConfigureAwait(false))!));
            fingerprint = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
        }
        if (Capture(path)?.Version != source.Version) return null;
        if (_fingerprints.Count == 64) _fingerprints.Clear();
        _fingerprints[source.Version] = fingerprint;
        return fingerprint;
    }

    private SqliteConnection Open()
    {
        var connection = database.GetConnection();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS archive_passwords (
                    fingerprint TEXT PRIMARY KEY, size INTEGER NOT NULL, secret BLOB NOT NULL);
                CREATE INDEX IF NOT EXISTS archive_passwords_size ON archive_passwords(size);
                """;
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static bool IsStorageFailure(Exception ex) => ex is IOException or UnauthorizedAccessException
        or SqliteException or CryptographicException;
}
