using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Util.Store;
using GoogleDriveDownloader.Application.Configuration;
using GoogleDriveDownloader.Infrastructure.Authentication;

namespace GoogleDriveDownloader.Tests;

public sealed class DpapiTokenStoreTests
{
    [Fact]
    public async Task SdkTokenResponseSurvivesRestartAndIsActualDpapiCiphertext()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        IDataStore store = scope.Configuration.CreateTokenStore();
        var token = new TokenResponse
        {
            AccessToken = "synthetic-access-token-content",
            RefreshToken = "synthetic-refresh-token-content",
            TokenType = "Bearer",
            ExpiresInSeconds = 3600,
            IssuedUtc = new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc),
            Scope = GoogleOAuthSettings.DriveReadonlyScope
        };

        await store.StoreAsync("user", token);

        var encrypted = await File.ReadAllBytesAsync(scope.TokenPath);
        Assert.DoesNotContain(token.AccessToken, Encoding.UTF8.GetString(encrypted));
        Assert.DoesNotContain(token.RefreshToken, Encoding.UTF8.GetString(encrypted));
        var entropy = Entropy(scope.Settings.GoogleOAuth);
        var plaintext = ProtectedData.Unprotect(encrypted, entropy, DataProtectionScope.CurrentUser);
        try
        {
            Assert.Contains(token.RefreshToken, Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        var restarted = scope.NewConfiguration(scope.Settings.GoogleOAuth);
        await restarted.LoadAsync();
        var restored = await restarted.CreateTokenStore().GetAsync<TokenResponse>("user");
        Assert.Equal(token.AccessToken, restored.AccessToken);
        Assert.Equal(token.RefreshToken, restored.RefreshToken);
        Assert.Equal(token.IssuedUtc, restored.IssuedUtc);
        Assert.Equal(token.ExpiresInSeconds, restored.ExpiresInSeconds);
        Assert.Empty(Directory.GetFiles(scope.UserDirectory, "*.tmp"));
    }

    [Fact]
    public async Task TypeAndKeyAreIndependentAndDoNotBecomeFilePaths()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        var store = scope.Configuration.CreateTokenStore();
        const string key = "../../outside";

        await store.StoreAsync(key, "text");
        await store.StoreAsync(key, 42);
        await store.StoreAsync("another", "preserve");
        await store.DeleteAsync<string>(key);

        Assert.Null(await store.GetAsync<string>(key));
        Assert.Equal(42, await store.GetAsync<int>(key));
        Assert.Equal("preserve", await store.GetAsync<string>("another"));
        await store.ClearAsync();
        Assert.False(File.Exists(scope.TokenPath));
        Assert.Null(await store.GetAsync<string>("another"));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("secret")]
    [InlineData("scope")]
    public async Task ChangingAnyBindingComponentInvalidatesPersistedTokens(string component)
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        await scope.Configuration.CreateTokenStore().StoreAsync("user", "synthetic-old-token");
        var original = scope.Settings.GoogleOAuth;
        var changed = new GoogleOAuthSettings
        {
            ClientId = component == "id" ? "synthetic-different-client" : original.ClientId,
            ClientSecret = component == "secret" ? "synthetic-different-secret" : original.ClientSecret,
            Scope = component == "scope" ? "synthetic-different-scope" : original.Scope
        };
        var configuration = scope.NewConfiguration(changed);

        await configuration.LoadAsync();

        Assert.Null(await configuration.CreateTokenStore().GetAsync<string>("user"));
        Assert.False(File.Exists(scope.TokenPath));
    }

    [Fact]
    public async Task ImportInvalidatesOldStoreIncludingWritesDeletesAndClear()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        var oldStore = scope.Configuration.CreateTokenStore();
        await oldStore.StoreAsync("user", "synthetic-old-token");

        await scope.Configuration.ImportAsync(await scope.SourceAsync());

        Assert.False(File.Exists(scope.TokenPath));
        Assert.Null(await oldStore.GetAsync<string>("user"));
        await Assert.ThrowsAsync<AuthStorageException>(() => oldStore.StoreAsync("user", "must-not-persist"));
        var newStore = scope.Configuration.CreateTokenStore();
        await newStore.StoreAsync("user", "synthetic-new-token");
        await oldStore.ClearAsync();
        await oldStore.DeleteAsync<string>("user");
        Assert.Equal("synthetic-new-token", await newStore.GetAsync<string>("user"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("broken synthetic-access-token")]
    [InlineData("{\"access_token\":\"synthetic-plaintext\"}")]
    public async Task CorruptOrPlaintextCacheIsDiscardedWithoutLeakingContents(string contents)
    {
        await using var scope = new AuthTestScope(configured: true);
        Directory.CreateDirectory(scope.UserDirectory);
        await File.WriteAllTextAsync(scope.TokenPath, contents);

        await scope.Configuration.LoadAsync();

        Assert.Null(await scope.Configuration.CreateTokenStore().GetAsync<TokenResponse>("user"));
        Assert.False(File.Exists(scope.TokenPath));
        await scope.Logging.DisposeAsync();
        Assert.Contains(scope.Entries, entry => entry.Context.Status == "AuthorizationRequired");
        Assert.DoesNotContain("synthetic-access-token", string.Join("\n", scope.Entries));
        Assert.DoesNotContain("synthetic-plaintext", string.Join("\n", scope.Entries));
    }

    [Fact]
    public async Task InvalidEncryptedEnvelopeIsAlsoDiscarded()
    {
        await using var scope = new AuthTestScope(configured: true);
        Directory.CreateDirectory(scope.UserDirectory);
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes("{broken}"),
            Entropy(scope.Settings.GoogleOAuth), DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(scope.TokenPath, encrypted);

        await scope.Configuration.LoadAsync();

        Assert.False(File.Exists(scope.TokenPath));
    }

    [Fact]
    public async Task MissingCacheIsNormalAndUnavailableCacheDoesNotCrashReads()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        var store = scope.Configuration.CreateTokenStore();
        Assert.Null(await store.GetAsync<string>("user"));
        await store.StoreAsync("user", "synthetic-token");
        using var locked = new FileStream(scope.TokenPath, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Null(await store.GetAsync<string>("user"));
    }

    [Fact]
    public async Task FailedAtomicWritePreservesPreviousCiphertextAndCleansTemporaryFile()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        var store = scope.Configuration.CreateTokenStore();
        await store.StoreAsync("user", "synthetic-original-token");
        var original = await File.ReadAllBytesAsync(scope.TokenPath);

        using (var locked = new FileStream(scope.TokenPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAsync<AuthStorageException>(() => store.StoreAsync("user", "synthetic-replacement-token"));
        }

        Assert.Equal(original, await File.ReadAllBytesAsync(scope.TokenPath));
        Assert.Equal("synthetic-original-token", await store.GetAsync<string>("user"));
        Assert.Empty(Directory.GetFiles(scope.UserDirectory, "*.tmp"));
    }

    [Fact]
    public async Task ConcurrentWritesThroughSeparateBoundStoresDoNotLoseKeys()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(index =>
            scope.Configuration.CreateTokenStore().StoreAsync(index.ToString(), index)));

        var store = scope.Configuration.CreateTokenStore();
        for (var index = 0; index < 20; index++)
        {
            Assert.Equal(index, await store.GetAsync<int>(index.ToString()));
        }
    }

    [Fact]
    public async Task CancelledWriteLeavesExistingTokenUntouched()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        var store = scope.Configuration.CreateTokenStore();
        await store.StoreAsync("user", "original");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.StoreAsync("user", "replacement", cancellation.Token));

        Assert.Equal("original", await store.GetAsync<string>("user"));
    }

    private static byte[] Entropy(GoogleOAuthSettings settings) => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[]
    {
        "GoogleDriveDownloader.TokenStore.v1", settings.ClientId, settings.ClientSecret, settings.Scope
    }));
}
