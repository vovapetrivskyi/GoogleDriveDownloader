using System.Net;
using System.Security.Cryptography;
using System.Text;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Http;
using GoogleDriveDownloader.Application.Configuration;
using GoogleDriveDownloader.Infrastructure.Authentication;

namespace GoogleDriveDownloader.Tests;

public sealed class GoogleAuthServiceTests
{
    [Fact]
    public async Task BrokerSendsPkceS256OfflineReadonlyScopeAndStateWithProtectedPersistence()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        using var transport = new OAuthTestTransport();
        var receiver = new OAuthTestReceiver();
        await using var auth = Create(scope, transport, receiver);

        await auth.SignInAsync();

        Assert.True(auth.IsAuthorized);
        Assert.Equal(1, receiver.Calls);
        var query = receiver.Request!;
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("offline", query["access_type"]);
        Assert.Equal(GoogleOAuthSettings.DriveReadonlyScope, query["scope"]);
        Assert.Equal("consent select_account", query["prompt"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(receiver.RedirectUri, query["redirect_uri"]);
        Assert.Equal(64, query["state"].Length);
        var request = Assert.Single(transport.Requests);
        Assert.Equal("authorization_code", request["grant_type"]);
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(request["code_verifier"]));
        Assert.Equal(Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_'), query["code_challenge"]);
        var stored = await scope.Configuration.CreateTokenStore().GetAsync<TokenResponse>(GoogleAuthService.UserId);
        Assert.Equal("synthetic-refresh-new", stored.RefreshToken);
        Assert.DoesNotContain("synthetic-refresh-new", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(scope.TokenPath)));
        Assert.Equal(TimeSpan.FromSeconds(scope.Settings.Download.RequestTimeoutSeconds), Assert.Single(transport.Clients).Timeout);
    }

    [Fact]
    public async Task RestartRestoresWithoutBrowserAndRefreshPreservesRefreshToken()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        await SeedAsync(scope, expired: true);
        using var transport = new OAuthTestTransport
        {
            RespondAsync = (_, _) => Task.FromResult(OAuthTestTransport.Success(refresh: null))
        };
        var receiver = new OAuthTestReceiver();
        await using (var auth = Create(scope, transport, receiver))
        {
            Assert.True(await auth.RestoreAsync());
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files");
            await auth.InterceptAsync(request, default);
            Assert.Equal("synthetic-access-new", request.Headers.Authorization!.Parameter);
            Assert.Equal("refresh_token", Assert.Single(transport.Requests)["grant_type"]);
        }
        var configuration = scope.NewConfiguration(scope.Settings.GoogleOAuth);
        await configuration.LoadAsync();
        await using var restarted = new GoogleAuthService(scope.Settings, configuration, scope.Logging, () => receiver, transport);
        Assert.True(await restarted.RestoreAsync());
        Assert.Equal(0, receiver.Calls);
        Assert.Single(transport.Requests);
        var persisted = await configuration.CreateTokenStore().GetAsync<TokenResponse>(GoogleAuthService.UserId);
        Assert.Equal("synthetic-refresh-old", persisted.RefreshToken);
    }

    [Fact]
    public async Task AutomaticRefreshDuringRequestIsSerializedForConcurrentCallers()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        await SeedAsync(scope, expired: true);
        using var transport = new OAuthTestTransport();
        await using var auth = Create(scope, transport, new OAuthTestReceiver());
        Assert.True(await auth.RestoreAsync());
        await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files");
            await auth.InterceptAsync(request, default);
            Assert.Equal("synthetic-access-new", request.Headers.Authorization!.Parameter);
        }));
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task InvalidRefreshOnStartupClearsCacheAndRequestsExplicitSignIn()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        await SeedAsync(scope, expired: true);
        using var transport = new OAuthTestTransport { RespondAsync = (_, _) => Task.FromResult(OAuthTestTransport.Error("invalid_grant")) };
        var receiver = new OAuthTestReceiver();
        await using var auth = Create(scope, transport, receiver);

        var exception = await Assert.ThrowsAsync<AuthenticationException>(() => auth.RestoreAsync());

        Assert.DoesNotContain("DO-NOT-EXPOSE", exception.ToString());
        Assert.False(auth.IsAuthorized);
        Assert.Equal(AuthenticationStatus.AuthorizationRequired, auth.Status);
        Assert.False(File.Exists(scope.TokenPath));
        Assert.Equal(0, receiver.Calls);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task ExplicitSignInAfterInvalidRefreshUsesOnlyOneBrowserAttempt()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        await SeedAsync(scope, expired: true);
        using var transport = new OAuthTestTransport
        {
            RespondAsync = (form, _) => Task.FromResult(form["grant_type"] == "refresh_token"
                ? OAuthTestTransport.Error("invalid_grant") : OAuthTestTransport.Success())
        };
        var receiver = new OAuthTestReceiver();
        await using var auth = Create(scope, transport, receiver);
        await auth.SignInAsync();
        Assert.True(auth.IsAuthorized);
        Assert.Equal(1, receiver.Calls);
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task InvalidGrantDuringCodeExchangeDoesNotRepeatBrowser()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        using var transport = new OAuthTestTransport { RespondAsync = (_, _) => Task.FromResult(OAuthTestTransport.Error("invalid_grant")) };
        var receiver = new OAuthTestReceiver();
        await using var auth = Create(scope, transport, receiver);
        await Assert.ThrowsAsync<AuthenticationException>(() => auth.SignInAsync());
        Assert.Equal(1, receiver.Calls);
        Assert.Single(transport.Requests);
        Assert.False(File.Exists(scope.TokenPath));
        Assert.False(auth.IsAuthorized);
    }

    [Fact]
    public async Task SignOutInvalidatesPreviousStoresAndBlocksAuthorizedRequests()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        await SeedAsync(scope);
        var oldStore = scope.Configuration.CreateTokenStore();
        using var transport = new OAuthTestTransport();
        await using var auth = Create(scope, transport, new OAuthTestReceiver());
        Assert.True(await auth.RestoreAsync());
        await auth.SignOutAsync();
        Assert.False(auth.IsAuthorized);
        Assert.False(File.Exists(scope.TokenPath));
        await Assert.ThrowsAsync<AuthStorageException>(() => oldStore.StoreAsync(GoogleAuthService.UserId, Token()));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files");
        await Assert.ThrowsAsync<AuthenticationException>(() => auth.InterceptAsync(request, default));
        Assert.Null(request.Headers.Authorization);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task AccountChangeClearsCacheBeforeBrowserAndUsesAccountSelection()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        await SeedAsync(scope);
        using var transport = new OAuthTestTransport();
        var receiver = new OAuthTestReceiver
        {
            ReceiveAsync = (url, _) =>
            {
                Assert.False(File.Exists(scope.TokenPath));
                return Task.FromResult(new AuthorizationCodeResponseUrl { Code = "new-account-code", State = url.State });
            }
        };
        await using var auth = Create(scope, transport, receiver);
        Assert.True(await auth.RestoreAsync());
        await auth.ChangeAccountAsync();
        Assert.True(auth.IsAuthorized);
        Assert.Contains("select_account", receiver.Request!["prompt"]);
        Assert.Equal("synthetic-refresh-new", (await scope.Configuration.CreateTokenStore().GetAsync<TokenResponse>(GoogleAuthService.UserId)).RefreshToken);
    }

    [Fact]
    public async Task ConfigurationImportInvalidatesInMemorySession()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        await SeedAsync(scope);
        using var transport = new OAuthTestTransport();
        await using var auth = Create(scope, transport, new OAuthTestReceiver());
        Assert.True(await auth.RestoreAsync());
        await scope.Configuration.ImportAsync(await scope.SourceAsync());
        Assert.False(auth.IsAuthorized);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files");
        await Assert.ThrowsAsync<AuthenticationException>(() => auth.InterceptAsync(request, default));
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StateMismatchOrAccessDenialNeverExchangesCode(bool denied)
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        using var transport = new OAuthTestTransport();
        var receiver = new OAuthTestReceiver
        {
            ReceiveAsync = (url, _) => Task.FromResult(denied
                ? new AuthorizationCodeResponseUrl { Error = "access_denied", ErrorDescription = "DO-NOT-EXPOSE", State = url.State }
                : new AuthorizationCodeResponseUrl { Code = "untrusted-code", State = "wrong-state" })
        };
        await using var auth = Create(scope, transport, receiver);
        var exception = await Assert.ThrowsAsync<AuthenticationException>(() => auth.SignInAsync());
        Assert.DoesNotContain("DO-NOT-EXPOSE", exception.ToString());
        Assert.Empty(transport.Requests);
        Assert.False(auth.IsAuthorized);
        Assert.False(File.Exists(scope.TokenPath));
    }

    [Fact]
    public async Task CancellationStopsReceiverAndLeavesNoSession()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        using var transport = new OAuthTestTransport();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = WaitingReceiver(entered);
        await using var auth = Create(scope, transport, receiver);
        using var cancellation = new CancellationTokenSource();
        var operation = auth.SignInAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.False(File.Exists(scope.TokenPath));
        Assert.False(auth.IsAuthorized);
        Assert.Empty(transport.Requests);
        receiver.ReceiveAsync = null;
        await auth.SignInAsync();
        Assert.True(auth.IsAuthorized);
    }

    [Fact]
    public async Task AuthorizationTimeoutStopsReceiverWithSafeMessage()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        using var transport = new OAuthTestTransport();
        var settings = new AppSettings { Download = new DownloadSettings { AuthorizationTimeoutSeconds = 1 } };
        await using var auth = new GoogleAuthService(settings, scope.Configuration, scope.Logging,
            () => WaitingReceiver(new TaskCompletionSource()), transport);
        var exception = await Assert.ThrowsAsync<AuthenticationException>(() => auth.SignInAsync());
        Assert.Contains("Час очікування", exception.Message);
        Assert.False(auth.IsAuthorized);
        Assert.False(File.Exists(scope.TokenPath));
    }

    [Fact]
    public async Task DisposalCancelsPendingBrowserAndReleasesHttpClient()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        using var transport = new OAuthTestTransport();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var auth = Create(scope, transport, WaitingReceiver(entered));
        var operation = auth.SignInAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await auth.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        var client = Assert.Single(transport.Clients);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetAsync("https://oauth2.googleapis.com/token"));
        Assert.False(File.Exists(scope.TokenPath));
    }

    [Fact]
    public async Task MissingConfigurationAndMissingCacheNeverOpenBrowserOnRestore()
    {
        await using var scope = new AuthTestScope();
        await scope.Configuration.LoadAsync();
        using var transport = new OAuthTestTransport();
        var receiver = new OAuthTestReceiver();
        await using var auth = Create(scope, transport, receiver);
        Assert.False(await auth.RestoreAsync());
        await Assert.ThrowsAsync<AuthenticationException>(() => auth.SignInAsync());
        await scope.Configuration.ImportAsync(await scope.SourceAsync());
        Assert.False(await auth.RestoreAsync());
        Assert.Empty(transport.Requests);
        Assert.Equal(0, receiver.Calls);
    }

    [Fact]
    public async Task Http401RefreshesOnceAndRepeatedRejectionRequiresNewSignIn()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        await SeedAsync(scope);
        using var transport = new OAuthTestTransport();
        await using var auth = Create(scope, transport, new OAuthTestReceiver());
        Assert.True(await auth.RestoreAsync());
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files");
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var args = new HandleUnsuccessfulResponseArgs { Request = request, Response = response, TotalTries = 3, CurrentFailedTry = 1, CancellationToken = default };
        Assert.True(await auth.HandleResponseAsync(args));
        await Assert.ThrowsAsync<AuthenticationException>(() => auth.HandleResponseAsync(args));
        Assert.Single(transport.Requests);
        Assert.False(auth.IsAuthorized);
        Assert.False(File.Exists(scope.TokenPath));
    }

    [Fact]
    public async Task EndpointErrorDoesNotExposeSensitiveResponseAndDoesNotDeleteRecoverableRefreshToken()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        await SeedAsync(scope, expired: true);
        using var transport = new OAuthTestTransport { RespondAsync = (_, _) => Task.FromResult(OAuthTestTransport.Error("temporarily_unavailable")) };
        await using var auth = Create(scope, transport, new OAuthTestReceiver());
        var exception = await Assert.ThrowsAsync<AuthenticationException>(() => auth.RestoreAsync());
        Assert.DoesNotContain("DO-NOT-EXPOSE", exception.ToString());
        Assert.True(File.Exists(scope.TokenPath));
        Assert.Single(transport.Requests);
        Assert.False(auth.IsAuthorized);
        await scope.Logging.DisposeAsync();
        var logs = string.Join("\n", await Task.WhenAll(Directory.GetFiles(Path.Combine(scope.UserDirectory, "logs")).Select(path => File.ReadAllTextAsync(path))));
        Assert.DoesNotContain("synthetic-access-old", logs);
        Assert.DoesNotContain("synthetic-refresh-old", logs);
        Assert.DoesNotContain("DO-NOT-EXPOSE", logs);
    }

    private static GoogleAuthService Create(AuthTestScope scope, OAuthTestTransport transport, OAuthTestReceiver receiver) =>
        new(scope.Settings, scope.Configuration, scope.Logging, () => receiver, transport);

    private static Task SeedAsync(AuthTestScope scope, bool expired = false) =>
        scope.Configuration.CreateTokenStore().StoreAsync(GoogleAuthService.UserId, Token(expired));

    private static TokenResponse Token(bool expired = false) => new()
    {
        AccessToken = "synthetic-access-old", RefreshToken = "synthetic-refresh-old", TokenType = "Bearer",
        Scope = GoogleOAuthSettings.DriveReadonlyScope, ExpiresInSeconds = 3600,
        IssuedUtc = expired ? DateTime.UtcNow.AddHours(-2) : DateTime.UtcNow
    };

    private static OAuthTestReceiver WaitingReceiver(TaskCompletionSource entered) => new()
    {
        ReceiveAsync = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        }
    };
}
