using Google.Apis.Http;

namespace GoogleDriveDownloader.Infrastructure.Authentication;

internal sealed class OAuthHttpClientFactory(IHttpClientFactory inner, TimeSpan timeout) : IHttpClientFactory, IDisposable
{
    private readonly List<ConfigurableHttpClient> _clients = [];

    public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args)
    {
        var client = inner.CreateHttpClient(args);
        client.Timeout = timeout;
        // В OAuth немає автоматичних повторів: invalid_grant потребує нового входу.
        client.MessageHandler.NumTries = 1;
        _clients.Add(client);
        return client;
    }

    public void Dispose()
    {
        // Broker не повертає flow при помилці; фабрика звільняє і такий HTTP-клієнт.
        foreach (var client in _clients)
        {
            client.Dispose();
        }
        _clients.Clear();
    }
}
