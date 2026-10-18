using System.Text.Json.Nodes;
using GoogleDriveDownloader.Infrastructure.Authentication;

namespace GoogleDriveDownloader.Tests;

public sealed class OAuthConfigurationServiceTests
{
    [Fact]
    public async Task EmptyInstallationRemainsUnconfiguredAndDoesNotCreateAuthFiles()
    {
        await using var scope = new AuthTestScope();

        await scope.Configuration.LoadAsync();

        Assert.False(scope.Configuration.Current.IsConfigured);
        Assert.False(File.Exists(scope.ClientPath));
        Assert.False(File.Exists(scope.TokenPath));
        Assert.Throws<AuthStorageException>(() => scope.Configuration.CreateTokenStore());
    }

    [Fact]
    public async Task ImportsDesktopFileOutsideExeAndKeepsSourceUnchanged()
    {
        await using var scope = new AuthTestScope();
        var source = await scope.SourceAsync();
        var original = await File.ReadAllBytesAsync(source);

        await scope.Configuration.ImportAsync(source);

        Assert.True(scope.Configuration.Current.IsConfigured);
        Assert.Equal(AuthTestScope.Client("B").ClientId, scope.Configuration.Current.ClientId);
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        Assert.True(File.Exists(scope.ClientPath));
        var reloaded = scope.NewConfiguration(AuthTestScope.Client("fallback"));
        await reloaded.LoadAsync();
        Assert.Equal(AuthTestScope.Client("B").ClientSecret, reloaded.Current.ClientSecret);
    }

    [Fact]
    public async Task MissingImportedFileUsesAppsettingsCredentials()
    {
        await using var scope = new AuthTestScope(configured: true);

        await scope.Configuration.LoadAsync();

        Assert.Equal(scope.Settings.GoogleOAuth.ClientId, scope.Configuration.Current.ClientId);
        Assert.False(File.Exists(scope.ClientPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IncompleteFallbackDoesNotPreventImportAndDoesNotBecomeActive(bool includeId)
    {
        await using var scope = new AuthTestScope();
        var incomplete = new GoogleDriveDownloader.Application.Configuration.GoogleOAuthSettings
        {
            ClientId = includeId ? "synthetic-incomplete-id" : "",
            ClientSecret = includeId ? "" : "synthetic-incomplete-secret"
        };
        var configuration = scope.NewConfiguration(incomplete);

        await configuration.LoadAsync();
        Assert.False(configuration.Current.IsConfigured);
        await configuration.ImportAsync(await scope.SourceAsync());

        var restarted = scope.NewConfiguration(incomplete);
        await restarted.LoadAsync();
        Assert.Equal(AuthTestScope.Client("B").ClientId, restarted.Current.ClientId);
    }

    [Theory]
    [InlineData("{\"type\":\"service_account\",\"private_key\":\"synthetic-private\"}")]
    [InlineData("{\"web\":{\"client_id\":\"synthetic\",\"client_secret\":\"synthetic\"}}")]
    [InlineData("{\"installed\":{\"client_id\":\"synthetic\"}}")]
    [InlineData("{\"installed\":{}}")]
    [InlineData("{\"installed\":{},\"web\":{}}")]
    [InlineData("{\"installed\":{},\"installed\":{}}")]
    [InlineData("{\"installed\":[]}")]
    [InlineData("[]")]
    [InlineData("{broken synthetic-secret}")]
    public async Task RejectsWrongClientTypesAndMalformedFilesWithoutChangingConfiguration(string json)
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        var original = scope.Configuration.Current;
        var source = await scope.SourceAsync(json);

        var exception = await Assert.ThrowsAsync<AuthStorageException>(() => scope.Configuration.ImportAsync(source));

        Assert.Same(original, scope.Configuration.Current);
        Assert.False(File.Exists(scope.ClientPath));
        Assert.DoesNotContain("synthetic-private", exception.ToString());
        Assert.DoesNotContain("synthetic-secret", exception.ToString());
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData("client_id", "")]
    [InlineData("client_secret", " ")]
    [InlineData("auth_uri", "https://example.invalid/auth")]
    [InlineData("token_uri", "https://example.invalid/token")]
    public async Task RejectsMissingCredentialsAndForeignEndpoints(string property, string value)
    {
        await using var scope = new AuthTestScope();
        var json = JsonNode.Parse(AuthTestScope.DesktopJson())!;
        json["installed"]![property] = value;
        var source = await scope.SourceAsync(json.ToJsonString());

        await Assert.ThrowsAsync<AuthStorageException>(() => scope.Configuration.ImportAsync(source));
    }

    [Theory]
    [InlineData("https://example.invalid/callback")]
    [InlineData("http://localhost.example.invalid/")]
    [InlineData("http://user@localhost/")]
    [InlineData("http://localhost/?token=synthetic")]
    public async Task RejectsNonLoopbackOrAmbiguousRedirect(string redirect)
    {
        await using var scope = new AuthTestScope();
        var json = JsonNode.Parse(AuthTestScope.DesktopJson())!;
        json["installed"]!["redirect_uris"] = new JsonArray(redirect);
        var source = await scope.SourceAsync(json.ToJsonString());

        await Assert.ThrowsAsync<AuthStorageException>(() => scope.Configuration.ImportAsync(source));
    }

    [Fact]
    public async Task AcceptsUtf8BomAndDoesNotCopyUnknownFields()
    {
        await using var scope = new AuthTestScope();
        var json = JsonNode.Parse(AuthTestScope.DesktopJson())!;
        json["installed"]!["access_token"] = "synthetic-not-to-copy";
        var source = await scope.SourceAsync("\uFEFF" + json.ToJsonString());

        await scope.Configuration.ImportAsync(source);

        Assert.DoesNotContain("synthetic-not-to-copy", await File.ReadAllTextAsync(scope.ClientPath));
    }

    [Fact]
    public async Task DuplicateCredentialPropertyIsRejected()
    {
        await using var scope = new AuthTestScope();
        var json = AuthTestScope.DesktopJson().Replace("\"client_id\":", "\"client_id\":\"duplicate\",\"client_id\":", StringComparison.Ordinal);
        var source = await scope.SourceAsync(json);

        await Assert.ThrowsAsync<AuthStorageException>(() => scope.Configuration.ImportAsync(source));
    }

    [Fact]
    public async Task CorruptImportedConfigurationDisablesAuthorizationInsteadOfFallingBack()
    {
        await using var scope = new AuthTestScope(configured: true);
        Directory.CreateDirectory(scope.UserDirectory);
        await File.WriteAllTextAsync(scope.ClientPath, "{broken}");

        await scope.Configuration.LoadAsync();

        Assert.False(scope.Configuration.Current.IsConfigured);
        Assert.Throws<AuthStorageException>(() => scope.Configuration.CreateTokenStore());
    }

    [Fact]
    public async Task InaccessibleImportedConfigurationKeepsApplicationUsableForReimport()
    {
        await using var scope = new AuthTestScope(configured: true);
        Directory.CreateDirectory(scope.UserDirectory);
        await File.WriteAllTextAsync(scope.ClientPath, AuthTestScope.DesktopJson());
        using (var locked = new FileStream(scope.ClientPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await scope.Configuration.LoadAsync();
            Assert.False(scope.Configuration.Current.IsConfigured);
        }

        await scope.Configuration.ImportAsync(await scope.SourceAsync());
        Assert.True(scope.Configuration.Current.IsConfigured);
    }

    [Fact]
    public async Task FailedAtomicImportPreservesPreviousCredentialsAndRemovesTemporaryFile()
    {
        await using var scope = new AuthTestScope();
        await scope.Configuration.ImportAsync(await scope.SourceAsync());
        var original = await File.ReadAllBytesAsync(scope.ClientPath);
        var current = scope.Configuration.Current;
        await scope.Configuration.CreateTokenStore().StoreAsync("user", "synthetic-token");
        var replacement = await scope.SourceAsync(AuthTestScope.DesktopJson("C"));

        using (var locked = new FileStream(scope.ClientPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var exception = await Assert.ThrowsAsync<AuthStorageException>(() => scope.Configuration.ImportAsync(replacement));
            Assert.DoesNotContain("synthetic-client-secret", exception.ToString());
        }

        Assert.Same(current, scope.Configuration.Current);
        Assert.Equal(original, await File.ReadAllBytesAsync(scope.ClientPath));
        Assert.False(File.Exists(scope.TokenPath));
        Assert.Empty(Directory.GetFiles(scope.UserDirectory, "*.tmp"));
    }

    [Fact]
    public async Task CancellationDoesNotChangeActiveConfiguration()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        var current = scope.Configuration.Current;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = await scope.SourceAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Configuration.ImportAsync(source, cancellation.Token));

        Assert.Same(current, scope.Configuration.Current);
        Assert.False(File.Exists(scope.ClientPath));
    }

    [Fact]
    public async Task ImportedSecretsAreRegisteredForBothLoggingOutputs()
    {
        await using var scope = new AuthTestScope();
        await scope.Configuration.ImportAsync(await scope.SourceAsync());
        scope.Logging.Write(GoogleDriveDownloader.Application.Logging.LogSeverity.Error,
            scope.Configuration.Current.ClientSecret, exception: new Exception(scope.Configuration.Current.ClientId));
        await scope.Logging.DisposeAsync();

        var text = string.Join("\n", scope.Entries);
        foreach (var path in Directory.GetFiles(Path.Combine(scope.UserDirectory, "logs")))
        {
            text += await File.ReadAllTextAsync(path);
        }

        Assert.DoesNotContain(AuthTestScope.Client("B").ClientSecret, text);
        Assert.DoesNotContain(AuthTestScope.Client("B").ClientId, text);
    }
}
