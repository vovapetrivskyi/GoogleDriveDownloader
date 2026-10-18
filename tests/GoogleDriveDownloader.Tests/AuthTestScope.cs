using System.Collections.Concurrent;
using System.Text.Json;
using GoogleDriveDownloader.Application.Configuration;
using GoogleDriveDownloader.Application.Logging;
using GoogleDriveDownloader.Infrastructure.Authentication;
using GoogleDriveDownloader.Infrastructure.Logging;

namespace GoogleDriveDownloader.Tests;

internal sealed class AuthTestScope : IAsyncDisposable
{
    private readonly TestDirectory _directory = new();

    public AuthTestScope(bool configured = false)
    {
        Settings = new AppSettings { GoogleOAuth = configured ? Client() : new GoogleOAuthSettings() };
        Logging = new LoggingService(Settings, UserDirectory);
        Logging.EntryLogged += Entries.Enqueue;
        Configuration = NewConfiguration(Settings.GoogleOAuth);
    }

    public string UserDirectory => Path.Combine(_directory.Path, "profile");
    public string ClientPath => Path.Combine(UserDirectory, "oauth-client.json");
    public string TokenPath => Path.Combine(UserDirectory, "auth.dat");
    public AppSettings Settings { get; }
    public LoggingService Logging { get; }
    public ConcurrentQueue<LogEntry> Entries { get; } = new();
    public OAuthConfigurationService Configuration { get; }

    public OAuthConfigurationService NewConfiguration(GoogleOAuthSettings settings) =>
        new(new AppSettings { GoogleOAuth = settings }, Logging, UserDirectory);

    public async Task<string> SourceAsync(string? json = null)
    {
        var path = Path.Combine(_directory.Path, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, json ?? DesktopJson());
        return path;
    }

    public static GoogleOAuthSettings Client(string suffix = "A", string? scope = null) => new()
    {
        ClientId = "synthetic-desktop-client-" + suffix,
        ClientSecret = "synthetic-client-secret-" + suffix,
        Scope = scope ?? GoogleOAuthSettings.DriveReadonlyScope
    };

    public static string DesktopJson(string suffix = "B") => JsonSerializer.Serialize(new
    {
        installed = new
        {
            client_id = Client(suffix).ClientId,
            client_secret = Client(suffix).ClientSecret,
            project_id = "synthetic-project",
            auth_uri = "https://accounts.google.com/o/oauth2/auth",
            token_uri = "https://oauth2.googleapis.com/token",
            redirect_uris = new[] { "http://localhost" }
        }
    });

    public async ValueTask DisposeAsync()
    {
        await Logging.DisposeAsync();
        _directory.Dispose();
    }
}
