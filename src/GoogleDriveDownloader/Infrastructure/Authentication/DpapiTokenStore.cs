using System.Security.Cryptography;
using System.Text.Json;
using Google.Apis.Json;
using Google.Apis.Util.Store;
using GoogleDriveDownloader.Application.Configuration;
using GoogleDriveDownloader.Application.Logging;

namespace GoogleDriveDownloader.Infrastructure.Authentication;

public sealed class DpapiTokenStore : IDataStore
{
    private readonly string _path;
    private readonly byte[] _entropy;
    private readonly SemaphoreSlim _gate;
    private readonly Func<bool> _isCurrent;
    private readonly ILoggingService _logging;

    internal DpapiTokenStore(string path, GoogleOAuthSettings settings, SemaphoreSlim gate,
        Func<bool> isCurrent, ILoggingService logging)
    {
        _path = path;
        _gate = gate;
        _isCurrent = isCurrent;
        _logging = logging;
        var identity = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            "GoogleDriveDownloader.TokenStore.v1", settings.ClientId, settings.ClientSecret, settings.Scope
        });
        try
        {
            _entropy = SHA256.HashData(identity);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(identity);
        }
    }

    public Task StoreAsync<T>(string key, T value) => StoreAsync(key, value, CancellationToken.None);

    public Task StoreAsync<T>(string key, T value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ExecuteAsync(async () =>
        {
            if (!_isCurrent())
            {
                throw new AuthStorageException("OAuth-конфігурація змінилася. Потрібні нове сховище та повторна авторизація.");
            }

            var data = await ReadCoreAsync(throwOnAccessError: true, cancellationToken).ConfigureAwait(false);
            data.Values[TypedKey<T>(key)] = NewtonsoftJsonSerializer.Instance.Serialize(value!);
            await WriteCoreAsync(data, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    public Task<T> GetAsync<T>(string key) => GetAsync<T>(key, CancellationToken.None);

    public Task<T> GetAsync<T>(string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ExecuteAsync(async () =>
        {
            if (!_isCurrent())
            {
                return default!;
            }

            var data = await ReadCoreAsync(throwOnAccessError: false, cancellationToken).ConfigureAwait(false);
            if (!data.Values.TryGetValue(TypedKey<T>(key), out var value))
            {
                return default!;
            }

            try
            {
                return NewtonsoftJsonSerializer.Instance.Deserialize<T>(value);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                DiscardInvalidFile();
                return default!;
            }
        }, cancellationToken);
    }

    public Task DeleteAsync<T>(string key) => DeleteAsync<T>(key, CancellationToken.None);

    public Task DeleteAsync<T>(string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ExecuteAsync(async () =>
        {
            if (!_isCurrent())
            {
                return true;
            }

            var data = await ReadCoreAsync(throwOnAccessError: true, cancellationToken).ConfigureAwait(false);
            if (data.Values.Remove(TypedKey<T>(key)))
            {
                await WriteCoreAsync(data, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }, cancellationToken);
    }

    public Task ClearAsync() => ClearAsync(CancellationToken.None);

    public Task ClearAsync(CancellationToken cancellationToken) => ExecuteAsync(() =>
    {
        if (_isCurrent())
        {
            AuthFile.DeleteIfPresent(_path);
        }

        return Task.FromResult(true);
    }, cancellationToken);

    internal Task ValidateAsync(CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        if (_isCurrent())
        {
            await ReadCoreAsync(throwOnAccessError: false, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }, cancellationToken);

    private Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await operation().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            CryptographicException or JsonException or InvalidDataException or Newtonsoft.Json.JsonException)
        {
            _logging.Write(LogSeverity.Warning, "Не вдалося змінити захищене сховище токенів.", new LogContext(Status: "TokenStorageUnavailable"));
            throw new AuthStorageException("Не вдалося записати або очистити локальні токени. Перевірте права доступу та вільне місце; облікові дані не змінено.");
        }
        finally
        {
            _gate.Release();
        }
    }, cancellationToken);

    private async Task<StoreData> ReadCoreAsync(bool throwOnAccessError, CancellationToken cancellationToken)
    {
        try
        {
            var encrypted = await AuthFile.ReadAsync(_path, cancellationToken).ConfigureAwait(false);
            if (encrypted.Length == 0)
            {
                throw new InvalidDataException("Empty token file.");
            }
            var plaintext = ProtectedData.Unprotect(encrypted, _entropy, DataProtectionScope.CurrentUser);
            try
            {
                var data = JsonSerializer.Deserialize<StoreData>(plaintext);
                if (data is null || data.Version != 1 || data.Values is null || data.Values.Values.Any(value => value is null))
                {
                    throw new InvalidDataException("Invalid token envelope.");
                }

                return data;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new StoreData();
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or InvalidDataException)
        {
            DiscardInvalidFile();
            return new StoreData();
        }
        catch (Exception exception) when (!throwOnAccessError && exception is IOException or UnauthorizedAccessException)
        {
            _logging.Write(LogSeverity.Warning, "Локальні токени недоступні. Стан авторизації не відновлено.", new LogContext(Status: "TokenStorageUnavailable"));
            return new StoreData();
        }
    }

    private async Task WriteCoreAsync(StoreData data, CancellationToken cancellationToken)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(data);
        byte[] encrypted;
        try
        {
            encrypted = ProtectedData.Protect(plaintext, _entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        // До файлового API надходить тільки DPAPI ciphertext, зокрема для тимчасового файла.
        await AuthFile.WriteAtomicAsync(_path, encrypted, cancellationToken).ConfigureAwait(false);
    }

    private void DiscardInvalidFile()
    {
        try
        {
            AuthFile.DeleteIfPresent(_path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logging.Write(LogSeverity.Warning, "Не вдалося видалити непридатний кеш токенів.", new LogContext(Status: "TokenStorageUnavailable"));
        }

        _logging.Write(LogSeverity.Warning, "Кеш токенів пошкоджений або належить іншій OAuth-конфігурації чи Windows-користувачу. Потрібна повторна авторизація.",
            new LogContext(Status: "AuthorizationRequired"));
    }

    private static string TypedKey<T>(string key) => typeof(T).FullName + ":" + key;

    private sealed class StoreData
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);
    }
}
