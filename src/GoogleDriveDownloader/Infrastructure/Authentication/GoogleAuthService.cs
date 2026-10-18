using System.Net;
using System.Net.Http.Headers;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Http;
using GoogleDriveDownloader.Application.Configuration;
using GoogleDriveDownloader.Application.Logging;

namespace GoogleDriveDownloader.Infrastructure.Authentication;

public sealed class GoogleAuthService : IConfigurableHttpClientInitializer, IHttpExecuteInterceptor,
    IHttpUnsuccessfulResponseHandler, IAsyncDisposable
{
    public const string UserId = "desktop-user";
    private readonly AppSettings _settings;
    private readonly OAuthConfigurationService _configuration;
    private readonly ILoggingService _logging;
    private readonly Func<ICodeReceiver> _receiverFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private volatile Session? _session;
    private volatile AuthenticationStatus _status;
    private volatile bool _disposed;

    public GoogleAuthService(AppSettings settings, OAuthConfigurationService configuration, ILoggingService logging,
        Func<ICodeReceiver>? receiverFactory = null, IHttpClientFactory? httpClientFactory = null)
    {
        _settings = settings;
        _configuration = configuration;
        _logging = logging;
        _receiverFactory = receiverFactory ?? (() => new LocalServerCodeReceiver(
            "<html><meta charset='utf-8'><body>Поверніться до Google Drive Downloader. Це вікно можна закрити.</body></html>",
            LocalServerCodeReceiver.CallbackUriChooserStrategy.ForceLoopbackIp));
        _httpClientFactory = httpClientFactory ?? new HttpClientFactory();
    }

    public event Action<AuthenticationStatus>? StatusChanged;

    public AuthenticationStatus Status => _session is { } session && !ReferenceEquals(session.Configuration, _configuration.Current)
        ? AuthenticationStatus.AuthorizationRequired
        : _session is null && _status == AuthenticationStatus.Authorized ? AuthenticationStatus.SignedOut : _status;

    public bool IsAuthorized => Status == AuthenticationStatus.Authorized;

    // Відновлення ніколи не відкриває браузер, зокрема при відкликаному refresh token.
    public Task<bool> RestoreAsync(CancellationToken cancellationToken = default) => ExecuteAsync(async token =>
    {
        if (!_configuration.Current.IsConfigured)
        {
            DropSession();
            SetStatus(AuthenticationStatus.SignedOut);
            return false;
        }
        RequireConfiguration();
        return await RestoreCoreAsync(token).ConfigureAwait(false);
    }, cancellationToken);

    public Task SignInAsync(CancellationToken cancellationToken = default) => ExecuteAsync(async token =>
    {
        RequireConfiguration();
        try
        {
            if (await RestoreCoreAsync(token).ConfigureAwait(false))
            {
                return true;
            }
        }
        catch (TokenResponseException exception) when (IsInvalidGrant(exception))
        {
            // Лише одна інтерактивна спроба після непридатного кеша, без рекурсивного повтору.
            await ResetCoreAsync().ConfigureAwait(false);
        }
        await AuthorizeCoreAsync(token).ConfigureAwait(false);
        return true;
    }, cancellationToken);

    public Task ChangeAccountAsync(CancellationToken cancellationToken = default) => ExecuteAsync(async token =>
    {
        RequireConfiguration();
        await ResetCoreAsync().ConfigureAwait(false);
        await AuthorizeCoreAsync(token).ConfigureAwait(false);
        return true;
    }, cancellationToken);

    public Task SignOutAsync(CancellationToken cancellationToken = default) => ExecuteAsync(async token =>
    {
        token.ThrowIfCancellationRequested();
        await ResetCoreAsync().ConfigureAwait(false);
        _logging.Write(LogSeverity.Information, "Виконано локальний вихід із Google-акаунта.", new LogContext(Status: "SignedOut"));
        return true;
    }, cancellationToken);

    // Drive SDK отримуватиме цей initializer, а не UserCredential, що переживає вихід з акаунта.
    public void Initialize(ConfigurableHttpClient httpClient)
    {
        httpClient.MessageHandler.AddExecuteInterceptor(this);
        httpClient.MessageHandler.AddUnsuccessfulResponseHandler(this);
    }

    public Task InterceptAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        ExecuteAsync(async token =>
        {
            request.Headers.Authorization = null;
            var session = RequireSession();
            ValidateRequestSession(request, session);
            var value = await session.Credential.GetAccessTokenForRequestAsync(cancellationToken: token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ValidateToken(session);
            request.Options.Set(RequestSessionKey, session);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", value);
            return true;
        }, cancellationToken);

    public Task<bool> HandleResponseAsync(HandleUnsuccessfulResponseArgs args)
    {
        if (args.Response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return Task.FromResult(false);
        }

        return ExecuteAsync(async token =>
        {
            var session = RequireSession();
            ValidateRequestSession(args.Request, session);
            // Одне оновлення на запит: повторний 401 не запускає цикл refresh/retry.
            // Навіть останній дозволений SDK-запит має скинути відхилену сесію.
            if (!args.SupportsRetry || (args.Request.Options.TryGetValue(RefreshedKey, out var refreshed) && refreshed))
            {
                await ResetCoreAsync().ConfigureAwait(false);
                SetStatus(AuthenticationStatus.AuthorizationRequired);
                throw new AuthenticationException("Google відхилив авторизацію. Увійдіть повторно через Google.");
            }
            args.Request.Options.Set(RefreshedKey, true);
            await session.Credential.RefreshTokenAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ValidateToken(session);
            return true;
        }, args.CancellationToken);
    }

    private static readonly HttpRequestOptionsKey<bool> RefreshedKey = new("GoogleDriveDownloader.AuthRefreshed");
    private static readonly HttpRequestOptionsKey<Session> RequestSessionKey = new("GoogleDriveDownloader.AuthSession");

    private static void ValidateRequestSession(HttpRequestMessage request, Session session)
    {
        // Повтор HTTP-запиту попереднього акаунта не може непомітно використати новий акаунт.
        if (request.Options.TryGetValue(RequestSessionKey, out var original) && !ReferenceEquals(original, session))
        {
            request.Headers.Authorization = null;
            throw new AuthenticationException("Акаунт змінено під час запиту. Повторіть операцію з поточною сесією.");
        }
    }

    private async Task<bool> RestoreCoreAsync(CancellationToken token)
    {
        if (_session is { } current && ReferenceEquals(current.Configuration, _configuration.Current))
        {
            await current.Credential.GetAccessTokenForRequestAsync(cancellationToken: token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ValidateToken(current);
            return true;
        }
        DropSession();
        var configuration = _configuration.Current;
        var store = _configuration.CreateTokenStore();
        var cached = await store.GetAsync<TokenResponse>(UserId, token).ConfigureAwait(false);
        if (cached is null || string.IsNullOrWhiteSpace(cached.RefreshToken))
        {
            if (cached is not null)
            {
                await ResetCoreAsync().ConfigureAwait(false);
            }
            SetStatus(AuthenticationStatus.SignedOut);
            return false;
        }
        RegisterTokens(cached);
        var factory = CreateFactory();
        var flow = new GoogleAuthorizationCodeFlow(Initializer(configuration, factory, store));
        var session = new Session(configuration, new UserCredential(flow, UserId, cached), factory);
        try
        {
            await session.Credential.GetAccessTokenForRequestAsync(cancellationToken: token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ValidateToken(session);
            _session = session;
            SetStatus(AuthenticationStatus.Authorized);
            return true;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private async Task AuthorizeCoreAsync(CancellationToken token)
    {
        var configuration = _configuration.Current;
        var factory = CreateFactory();
        UserCredential? credential = null;
        try
        {
            var store = _configuration.CreateTokenStore();
            credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                Initializer(configuration, factory, store),
                [GoogleOAuthSettings.DriveReadonlyScope], UserId, usePkce: true,
                taskCancellationToken: token, dataStore: store,
                codeReceiver: new StateCodeReceiver(_receiverFactory())).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var session = new Session(configuration, credential, factory);
            ValidateToken(session);
            DropSession();
            _session = session;
            SetStatus(AuthenticationStatus.Authorized);
        }
        catch
        {
            credential?.Flow.Dispose();
            factory.Dispose();
            // Не залишаємо частково завершену інтерактивну сесію у кеші.
            if (ReferenceEquals(configuration, _configuration.Current))
            {
                await ResetCoreAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    private GoogleAuthorizationCodeFlow.Initializer Initializer(GoogleOAuthSettings configuration,
        IHttpClientFactory factory, DpapiTokenStore store) => new()
    {
        ClientSecrets = new ClientSecrets { ClientId = configuration.ClientId, ClientSecret = configuration.ClientSecret },
        Scopes = [GoogleOAuthSettings.DriveReadonlyScope], DataStore = store, HttpClientFactory = factory,
        Prompt = "consent select_account"
    };

    private OAuthHttpClientFactory CreateFactory() => new(_httpClientFactory, TimeSpan.FromSeconds(_settings.Download.RequestTimeoutSeconds));

    private void ValidateToken(Session session)
    {
        if (!ReferenceEquals(session.Configuration, _configuration.Current))
        {
            throw new AuthenticationException("OAuth-конфігурацію змінено. Увійдіть повторно.");
        }
        var token = session.Credential.Token;
        RegisterTokens(token);
        if (string.IsNullOrWhiteSpace(token.AccessToken) || string.IsNullOrWhiteSpace(token.RefreshToken) ||
            token.ExpiresInSeconds is not > 0 ||
            (DateTime.UtcNow - token.IssuedUtc).TotalSeconds >= token.ExpiresInSeconds ||
            (!string.IsNullOrWhiteSpace(token.Scope) && token.Scope != GoogleOAuthSettings.DriveReadonlyScope))
        {
            throw new AuthenticationException("Google не надав придатну offline-сесію з дозволом drive.readonly. Повторіть вхід.");
        }
    }

    private void RegisterTokens(TokenResponse token) => _logging.RegisterSensitiveValues(token.AccessToken, token.RefreshToken, token.IdToken);

    private Session RequireSession()
    {
        if (_session is not { } session || !ReferenceEquals(session.Configuration, _configuration.Current))
        {
            DropSession();
            SetStatus(AuthenticationStatus.AuthorizationRequired);
            throw new AuthenticationException("Потрібен вхід через Google.");
        }
        return session;
    }

    private void RequireConfiguration()
    {
        if (!_configuration.Current.IsConfigured || _configuration.Current.Scope != GoogleOAuthSettings.DriveReadonlyScope)
        {
            throw new AuthenticationException("Спочатку налаштуйте OAuth Desktop Client із дозволом drive.readonly.");
        }
    }

    private async Task ResetCoreAsync()
    {
        DropSession();
        SetStatus(AuthenticationStatus.SignedOut);
        // Після початку виходу очищення не переривається скасуванням браузера чи закриттям форми.
        await _configuration.ResetAuthorizationAsync().ConfigureAwait(false);
    }

    private void DropSession()
    {
        var session = _session;
        _session = null;
        session?.Dispose();
    }

    private void SetStatus(AuthenticationStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(status);
        _logging.Write(LogSeverity.Information, status switch
        {
            AuthenticationStatus.Authorized => "Авторизовано через Google.",
            AuthenticationStatus.AuthorizationRequired => "Потрібна повторна авторизація через Google.",
            _ => "Не авторизовано."
        }, new LogContext(Status: status.ToString()));
    }

    private static bool IsInvalidGrant(TokenResponseException exception) => exception.Error?.Error == "invalid_grant";

    private Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(_settings.Download.AuthorizationTimeoutSeconds));
        var entered = false;
        try
        {
            await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            return await action(timeout.Token).ConfigureAwait(false);
        }
        catch (TokenResponseException exception) when (IsInvalidGrant(exception))
        {
            await ResetCoreAsync().ConfigureAwait(false);
            SetStatus(AuthenticationStatus.AuthorizationRequired);
            throw new AuthenticationException("Дозвіл Google закінчився або відкликаний. Увійдіть повторно через Google.");
        }
        catch (TokenResponseException exception)
        {
            _logging.Write(LogSeverity.Warning, "Google не завершив авторизацію. Повторіть спробу.", new LogContext(Status: "AuthorizationFailed"));
            throw new AuthenticationException(exception.Error?.Error == "access_denied"
                ? "Доступ не надано. Ви можете повторити вхід через Google."
                : "Google не завершив авторизацію. Перевірте налаштування OAuth і повторіть вхід.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new AuthenticationException("Час очікування авторизації або мережевого запиту вичерпано. Повторіть спробу.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or NotSupportedException or System.ComponentModel.Win32Exception or System.Net.Sockets.SocketException)
        {
            throw new AuthenticationException("Не вдалося виконати авторизацію. Перевірте мережу, системний браузер і доступність loopback-порту.");
        }
        finally
        {
            if (entered) _gate.Release();
        }
    }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try { DropSession(); }
        finally { _gate.Release(); }
        _lifetime.Dispose();
    }

    private sealed record Session(GoogleOAuthSettings Configuration, UserCredential Credential, OAuthHttpClientFactory Factory) : IDisposable
    {
        public void Dispose()
        {
            Credential.Flow.Dispose();
            Factory.Dispose();
        }
    }
}
