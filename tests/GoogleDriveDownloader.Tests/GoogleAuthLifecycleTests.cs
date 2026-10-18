using System.Net;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Http;
using GoogleDriveDownloader.Infrastructure.Authentication;

namespace GoogleDriveDownloader.Tests;

public sealed class GoogleAuthLifecycleTests
{
    [Fact]
    public async Task RequestAutomaticallyRefreshesTokenThatExpiresAfterSignIn()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        using var transport = new OAuthTestTransport
        {
            RespondAsync = (form, _) => Task.FromResult(form["grant_type"] == "authorization_code"
                ? OAuthTestTransport.Success("short-lived-access", "initial-refresh", expires: 1)
                : OAuthTestTransport.Success("refreshed-access", refresh: null))
        };
        await using var auth = new GoogleAuthService(scope.Settings, scope.Configuration, scope.Logging, () => new OAuthTestReceiver(), transport);
        await auth.SignInAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files");
        await auth.InterceptAsync(request, default);
        Assert.Equal("refreshed-access", request.Headers.Authorization!.Parameter);
        Assert.Equal(2, transport.Requests.Count);
        Assert.Equal("refresh_token", transport.Requests.Last()["grant_type"]);
        var stored = await scope.Configuration.CreateTokenStore().GetAsync<TokenResponse>(GoogleAuthService.UserId);
        Assert.Equal("initial-refresh", stored.RefreshToken);
    }

    [Fact]
    public async Task RefreshRevokedDuringRequestClearsSessionWithoutOpeningAnotherBrowser()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        using var transport = new OAuthTestTransport();
        var receiver = new OAuthTestReceiver();
        await using var auth = new GoogleAuthService(scope.Settings, scope.Configuration, scope.Logging, () => receiver, transport);
        await auth.SignInAsync();
        transport.RespondAsync = (_, _) => Task.FromResult(OAuthTestTransport.Error("invalid_grant"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files");
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var args = new HandleUnsuccessfulResponseArgs { Request = request, Response = response, TotalTries = 3, CurrentFailedTry = 1 };
        await Assert.ThrowsAsync<AuthenticationException>(() => auth.HandleResponseAsync(args));
        Assert.Equal(1, receiver.Calls);
        Assert.Equal(2, transport.Requests.Count);
        Assert.Equal(AuthenticationStatus.AuthorizationRequired, auth.Status);
        Assert.False(File.Exists(scope.TokenPath));
    }

    [Fact]
    public async Task FailedTokenDeletionEndsInMemorySessionAndBlocksReuseUntilSuccessfulReset()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        using var transport = new OAuthTestTransport();
        await using var auth = new GoogleAuthService(scope.Settings, scope.Configuration, scope.Logging, () => new OAuthTestReceiver(), transport);
        await auth.SignInAsync();
        var staleStore = scope.Configuration.CreateTokenStore();
        using (var locked = new FileStream(scope.TokenPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<AuthStorageException>(() => auth.SignOutAsync());
            Assert.False(auth.IsAuthorized);
            Assert.Throws<AuthStorageException>(() => scope.Configuration.CreateTokenStore());
            await Assert.ThrowsAsync<AuthStorageException>(() => staleStore.StoreAsync(GoogleAuthService.UserId, new TokenResponse()));
        }
        await auth.SignOutAsync();
        Assert.False(File.Exists(scope.TokenPath));
        Assert.False(await auth.RestoreAsync());
    }

    [Fact]
    public async Task ImportDuringTokenExchangeCannotOverwriteOrClearNewClientCache()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new OAuthTestTransport
        {
            RespondAsync = async (_, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return OAuthTestTransport.Success();
            }
        };
        await using var auth = new GoogleAuthService(scope.Settings, scope.Configuration, scope.Logging, () => new OAuthTestReceiver(), transport);
        var operation = auth.SignInAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scope.Configuration.ImportAsync(await scope.SourceAsync());
        await scope.Configuration.CreateTokenStore().StoreAsync(GoogleAuthService.UserId, new TokenResponse { RefreshToken = "new-client-refresh" });
        release.SetResult();
        await Assert.ThrowsAsync<AuthStorageException>(() => operation);
        Assert.False(auth.IsAuthorized);
        Assert.Equal("new-client-refresh", (await scope.Configuration.CreateTokenStore().GetAsync<TokenResponse>(GoogleAuthService.UserId)).RefreshToken);
    }

    [Fact]
    public async Task MissingOfflineTokenIsRejectedAndNotPersisted()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        using var transport = new OAuthTestTransport { RespondAsync = (_, _) => Task.FromResult(OAuthTestTransport.Success(refresh: null)) };
        await using var auth = new GoogleAuthService(scope.Settings, scope.Configuration, scope.Logging, () => new OAuthTestReceiver(), transport);
        await Assert.ThrowsAsync<AuthenticationException>(() => auth.SignInAsync());
        Assert.False(auth.IsAuthorized);
        Assert.False(File.Exists(scope.TokenPath));
    }

    [Fact]
    public async Task NetworkFailureIsSanitizedAndAllowsSubsequentSignIn()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        using var transport = new OAuthTestTransport
        {
            RespondAsync = (_, _) => throw new HttpRequestException("DO-NOT-EXPOSE access_token=private-response")
        };
        await using var auth = new GoogleAuthService(scope.Settings, scope.Configuration, scope.Logging, () => new OAuthTestReceiver(), transport);
        var exception = await Assert.ThrowsAsync<AuthenticationException>(() => auth.SignInAsync());
        Assert.DoesNotContain("DO-NOT-EXPOSE", exception.ToString());
        Assert.Null(exception.InnerException);
        Assert.False(File.Exists(scope.TokenPath));
        transport.RespondAsync = null;
        await auth.SignInAsync();
        Assert.True(auth.IsAuthorized);
    }

    [Fact]
    public async Task SdkInitializerAddsBearerAndHandles401UsingFakeHttpPipeline()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        using var transport = new OAuthTestTransport();
        await using var auth = new GoogleAuthService(scope.Settings, scope.Configuration, scope.Logging, () => new OAuthTestReceiver(), transport);
        await auth.SignInAsync();
        using var handler = new ProtectedResourceHandler();
        using var client = new ConfigurableHttpClient(new ConfigurableMessageHandler(handler));
        auth.Initialize(client);
        using var response = await client.GetAsync("https://www.googleapis.com/drive/v3/files");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task RetryingOldRequestAfterAccountChangeCannotUseOrInvalidateNewSession()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        using var transport = new OAuthTestTransport();
        await using var auth = new GoogleAuthService(scope.Settings, scope.Configuration, scope.Logging, () => new OAuthTestReceiver(), transport);
        await auth.SignInAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files");
        await auth.InterceptAsync(request, default);
        await auth.ChangeAccountAsync();
        await Assert.ThrowsAsync<AuthenticationException>(() => auth.InterceptAsync(request, default));
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var args = new HandleUnsuccessfulResponseArgs { Request = request, Response = response, TotalTries = 3, CurrentFailedTry = 1 };
        await Assert.ThrowsAsync<AuthenticationException>(() => auth.HandleResponseAsync(args));
        Assert.True(auth.IsAuthorized);
        Assert.Null(request.Headers.Authorization);
        Assert.Equal(2, transport.Requests.Count);
    }

    private sealed class ProtectedResourceHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("synthetic-access-new", request.Headers.Authorization.Parameter);
            return Task.FromResult(new HttpResponseMessage(Calls == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK));
        }
    }
}
