using System.Text.Json;
using GoogleDriveDownloader.Application.Configuration;
using GoogleDriveDownloader.Application.Logging;

namespace GoogleDriveDownloader.Infrastructure.Authentication;

public sealed class OAuthConfigurationService
{
    private readonly GoogleOAuthSettings _fallback;
    private readonly ILoggingService _logging;
    private readonly string _clientPath;
    private readonly string _tokenPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GoogleOAuthSettings _current;
    private bool _loaded;

    public OAuthConfigurationService(AppSettings settings, ILoggingService logging, string? userDataDirectory = null)
    {
        _fallback = settings.GoogleOAuth;
        _current = new GoogleOAuthSettings { Scope = _fallback.Scope };
        _logging = logging;
        var directory = AuthFile.UserDirectory(userDataDirectory);
        _clientPath = Path.Combine(directory, "oauth-client.json");
        _tokenPath = Path.Combine(directory, "auth.dat");
    }

    public GoogleOAuthSettings Current => Volatile.Read(ref _current);

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        DpapiTokenStore? store;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GoogleOAuthSettings candidate;
            try
            {
                candidate = OAuthDesktopJson.Parse(await AuthFile.ReadAsync(_clientPath, cancellationToken).ConfigureAwait(false), _fallback.Scope);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                candidate = _fallback;
                // Парність резервних credentials перевіряємо лише за відсутності імпортованого
                // клієнта: неповний appsettings не повинен перекривати чинний імпорт.
                if (!candidate.IsConfigured &&
                    (!string.IsNullOrWhiteSpace(candidate.ClientId) || !string.IsNullOrWhiteSpace(candidate.ClientSecret)))
                {
                    candidate = new GoogleOAuthSettings { Scope = _fallback.Scope };
                    _logging.Write(LogSeverity.Warning, "OAuth credentials у appsettings.json неповні. Заповніть обидва поля або імпортуйте Desktop JSON.",
                        new LogContext(Status: "OAuthConfigurationUnavailable"));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or AuthStorageException)
            {
                candidate = new GoogleOAuthSettings { Scope = _fallback.Scope };
                _logging.Write(LogSeverity.Warning, "Імпортована OAuth-конфігурація недоступна або пошкоджена. Повторіть імпорт Desktop JSON; авторизацію не відновлено.",
                    new LogContext(Status: "OAuthConfigurationUnavailable"));
            }

            if (_loaded && !SameConfiguration(Current, candidate))
            {
                DeleteTokens();
            }

            PublishConfiguration(candidate, preserveUnchanged: true);
            store = Current.IsConfigured ? CreateTokenStore() : null;
            if (store is null)
            {
                DeleteTokens();
            }
        }
        finally
        {
            _gate.Release();
        }

        if (store is not null)
        {
            await store.ValidateAsync(cancellationToken).ConfigureAwait(false);
        }
    }, cancellationToken);

    public Task ImportAsync(string sourcePath, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        try
        {
            var candidate = OAuthDesktopJson.Parse(await AuthFile.ReadAsync(sourcePath, cancellationToken).ConfigureAwait(false), _fallback.Scope);
            _logging.RegisterSensitiveValues(candidate.ClientId, candidate.ClientSecret);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Спочатку інвалідуємо токени: збій запису credentials може вимагати нового входу,
                // але не дозволяє використати старий токен для нового клієнта.
                AuthFile.DeleteIfPresent(_tokenPath);
                await AuthFile.WriteAtomicAsync(_clientPath, OAuthDesktopJson.Serialize(candidate), cancellationToken).ConfigureAwait(false);
                PublishConfiguration(candidate, preserveUnchanged: false);
                _logging.Write(LogSeverity.Information, "Імпортовано OAuth Desktop Client. Потрібна повторна авторизація.",
                    new LogContext(Status: "OAuthConfigurationImported"));
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            throw new AuthStorageException("Не вдалося імпортувати OAuth Desktop JSON. Перевірте файл, його формат, права доступу та вільне місце. Попередня конфігурація залишилася чинною.");
        }
    }, cancellationToken);

    public DpapiTokenStore CreateTokenStore()
    {
        var configuration = Current;
        if (!Volatile.Read(ref _loaded) || !configuration.IsConfigured)
        {
            throw new AuthStorageException("Спочатку налаштуйте або імпортуйте OAuth Desktop Client.");
        }

        // Незмінний знімок є також поколінням конфігурації: попередній екземпляр
        // IDataStore після імпорту не може записати чи очистити новий кеш.
        return new DpapiTokenStore(_tokenPath, configuration, _gate,
            () => ReferenceEquals(configuration, Current), _logging);
    }

    private void PublishConfiguration(GoogleOAuthSettings candidate, bool preserveUnchanged)
    {
        _logging.RegisterSensitiveValues(candidate.ClientId, candidate.ClientSecret);
        if (!preserveUnchanged || !SameConfiguration(Current, candidate))
        {
            Volatile.Write(ref _current, candidate);
        }

        Volatile.Write(ref _loaded, true);
    }

    private void DeleteTokens()
    {
        try
        {
            AuthFile.DeleteIfPresent(_tokenPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logging.Write(LogSeverity.Warning, "Не вдалося очистити локальні токени. Перевірте права доступу до сховища.",
                new LogContext(Status: "TokenStorageUnavailable"));
        }
    }

    private static bool SameConfiguration(GoogleOAuthSettings first, GoogleOAuthSettings second) =>
        first.ClientId == second.ClientId && first.ClientSecret == second.ClientSecret && first.Scope == second.Scope;
}
