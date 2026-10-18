using System.Text.Json;
using GoogleDriveDownloader.Application.Configuration;

namespace GoogleDriveDownloader.Infrastructure.Authentication;

internal static class OAuthDesktopJson
{
    internal static GoogleOAuthSettings Parse(byte[] bytes, string scope)
    {
        // Підтримуємо також UTF-8 BOM; довільні адреси з JSON не передаємо до Google SDK.
        var json = bytes.AsMemory();
        if (json.Span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            json = json[3..];
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
            !root.TryGetProperty("installed", out var installed) || installed.ValueKind != JsonValueKind.Object)
        {
            throw InvalidClient();
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in installed.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw InvalidClient();
            }
        }

        var clientId = RequiredString(installed, "client_id");
        var clientSecret = RequiredString(installed, "client_secret");
        var authUri = RequiredString(installed, "auth_uri");
        var tokenUri = RequiredString(installed, "token_uri");
        if ((authUri != "https://accounts.google.com/o/oauth2/auth" &&
             authUri != "https://accounts.google.com/o/oauth2/v2/auth") ||
            tokenUri != "https://oauth2.googleapis.com/token" ||
            !installed.TryGetProperty("redirect_uris", out var redirects) ||
            redirects.ValueKind != JsonValueKind.Array || redirects.GetArrayLength() == 0)
        {
            throw InvalidClient();
        }

        foreach (var redirect in redirects.EnumerateArray())
        {
            if (redirect.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(redirect.GetString(), UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                throw InvalidClient();
            }
        }

        return new GoogleOAuthSettings { ClientId = clientId, ClientSecret = clientSecret, Scope = scope };
    }

    internal static byte[] Serialize(GoogleOAuthSettings settings) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        installed = new
        {
            client_id = settings.ClientId,
            client_secret = settings.ClientSecret,
            auth_uri = "https://accounts.google.com/o/oauth2/auth",
            token_uri = "https://oauth2.googleapis.com/token",
            redirect_uris = new[] { "http://localhost" }
        }
    });

    private static string RequiredString(JsonElement installed, string name)
    {
        if (!installed.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw InvalidClient();
        }

        var value = property.GetString()!.Trim();
        if (value.Any(char.IsControl))
        {
            throw InvalidClient();
        }

        return value;
    }

    private static AuthStorageException InvalidClient() => new(
        "Потрібен повний OAuth JSON типу Desktop Client із секцією installed, Google endpoints і loopback redirect. Service Account і Web Client не підтримуються.");
}
