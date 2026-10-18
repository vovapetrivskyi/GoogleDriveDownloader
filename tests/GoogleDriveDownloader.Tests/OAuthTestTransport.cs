using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Http;

namespace GoogleDriveDownloader.Tests;

internal sealed class OAuthTestTransport : IHttpClientFactory, IDisposable
{
    public ConcurrentQueue<Dictionary<string, string>> Requests { get; } = new();
    public ConcurrentQueue<ConfigurableHttpClient> Clients { get; } = new();
    public Func<Dictionary<string, string>, CancellationToken, Task<HttpResponseMessage>>? RespondAsync { get; set; }

    public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args)
    {
        var client = new ConfigurableHttpClient(new ConfigurableMessageHandler(new Handler(this)));
        foreach (var initializer in args.Initializers) initializer.Initialize(client);
        Clients.Enqueue(client);
        return client;
    }

    public static HttpResponseMessage Success(string access = "synthetic-access-new", string? refresh = "synthetic-refresh-new", int expires = 3600) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
            {
                access_token = access, refresh_token = refresh, expires_in = expires,
                token_type = "Bearer", scope = "https://www.googleapis.com/auth/drive.readonly"
            }), Encoding.UTF8, "application/json")
        };

    public static HttpResponseMessage Error(string error) => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
        {
            error, error_description = "DO-NOT-EXPOSE synthetic-sensitive-server-details"
        }), Encoding.UTF8, "application/json")
    };

    public static Dictionary<string, string> Parse(string value) => value.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0]),
            pair => Uri.UnescapeDataString(pair.Length == 2 ? pair[1].Replace('+', ' ') : ""));

    public void Dispose()
    {
        foreach (var client in Clients) client.Dispose();
    }

    private sealed class Handler(OAuthTestTransport owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://oauth2.googleapis.com/token", request.RequestUri!.AbsoluteUri);
            var form = Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            owner.Requests.Enqueue(form);
            return owner.RespondAsync is { } respond
                ? await respond(form, cancellationToken) : Success();
        }
    }
}

internal sealed class OAuthTestReceiver : ICodeReceiver
{
    public string RedirectUri => "http://127.0.0.1:49152/authorize/";
    public int Calls { get; private set; }
    public Dictionary<string, string>? Request { get; private set; }
    public Func<AuthorizationCodeRequestUrl, CancellationToken, Task<AuthorizationCodeResponseUrl>>? ReceiveAsync { get; set; }

    public async Task<AuthorizationCodeResponseUrl> ReceiveCodeAsync(AuthorizationCodeRequestUrl url, CancellationToken cancellationToken)
    {
        Calls++;
        Request = OAuthTestTransport.Parse(url.Build().Query);
        return ReceiveAsync is { } receive ? await receive(url, cancellationToken)
            : new AuthorizationCodeResponseUrl { Code = "synthetic-authorization-code", State = url.State };
    }
}
