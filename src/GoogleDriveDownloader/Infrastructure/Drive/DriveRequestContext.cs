using Google.Apis.Http;
using GoogleDriveDownloader.Application.Input;

namespace GoogleDriveDownloader.Infrastructure.Drive;

internal sealed class DriveRequestContext(DriveLink link)
{
    private static readonly HttpRequestOptionsKey<DriveRequestContext> ContextKey = new("GoogleDriveDownloader.DriveContext");
    public TimeSpan? RetryAfter { get; private set; }
    public int? HttpStatusCode { get; private set; }

    public void Apply(HttpRequestMessage request)
    {
        request.Options.Set(ContextKey, this);
        if (link.ResourceKey is not null)
        {
            request.Headers.Remove("X-Goog-Drive-Resource-Keys");
            request.Headers.Add("X-Goog-Drive-Resource-Keys", $"{link.FileId}/{link.ResourceKey}");
        }
    }

    internal sealed class ResponseHandler : IHttpUnsuccessfulResponseHandler
    {
        public Task<bool> HandleResponseAsync(HandleUnsuccessfulResponseArgs args)
        {
            if (args.Request.Options.TryGetValue(ContextKey, out var context))
            {
                context.HttpStatusCode = (int)args.Response.StatusCode;
                var header = args.Response.Headers.RetryAfter;
                var delay = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null);
                context.RetryAfter = delay is { } value ? (value < TimeSpan.Zero ? TimeSpan.Zero : value) : null;
            }
            // Класифікуємо відповідь, але не виконуємо повторів: ними керуватиме пакетний сервіс.
            return Task.FromResult(false);
        }
    }
}
