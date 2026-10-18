using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Google.Apis.Http;

namespace GoogleDriveDownloader.Tests;

internal sealed class DriveTestTransport : IHttpClientFactory, IDisposable
{
    public ConcurrentQueue<Request> Requests { get; } = new();
    public List<ConfigurableHttpClient> Clients { get; } = [];
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> RespondAsync { get; set; } =
        (_, _) => Task.FromResult(Metadata());

    public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args)
    {
        var client = new ConfigurableHttpClient(new ConfigurableMessageHandler(new Handler(this)));
        foreach (var initializer in args.Initializers) initializer.Initialize(client);
        Clients.Add(client);
        return client;
    }

    public static HttpResponseMessage Metadata(string id = "File_1", string mime = "application/pdf", string? extension = "pdf",
        long? size = 4, bool trashed = false, bool? canDownload = true, string name = "original.pdf") => Json(new
        {
            id, name, mimeType = mime, fileExtension = extension, size = size?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            trashed, capabilities = canDownload is null ? null : new { canDownload }
        });

    public static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    public static HttpResponseMessage Error(HttpStatusCode status, string reason = "forbidden", int? retryAfter = null)
    {
        var response = Json(new { error = new { code = (int)status, message = "DO-NOT-EXPOSE private-server-text",
            errors = new[] { new { domain = "global", reason, message = "DO-NOT-EXPOSE private-details" } } } });
        response.StatusCode = status;
        if (retryAfter is { } seconds) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return response;
    }

    public static HttpResponseMessage Content(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    public void Dispose()
    {
        foreach (var client in Clients) client.Dispose();
    }

    internal sealed record Request(Uri Uri, string? ResourceKeys, string? Authorization);

    internal sealed class Authentication : IConfigurableHttpClientInitializer, IHttpExecuteInterceptor
    {
        public void Initialize(ConfigurableHttpClient httpClient) => httpClient.MessageHandler.AddExecuteInterceptor(this);
        public Task InterceptAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-drive-access");
            return Task.CompletedTask;
        }
    }

    private sealed class Handler(DriveTestTransport owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("www.googleapis.com", request.RequestUri!.Host);
            owner.Requests.Enqueue(new Request(request.RequestUri,
                request.Headers.TryGetValues("X-Goog-Drive-Resource-Keys", out var values) ? string.Join(",", values) : null,
                request.Headers.Authorization?.ToString()));
            return owner.RespondAsync(request, cancellationToken);
        }
    }
}

internal sealed class DriveSourceStream(long length) : Stream
{
    public long BytesRead { get; private set; }
    public int LargestRead { get; private set; }
    public bool Disposed { get; private set; }
    public long? FailAfter { get; init; }
    public TaskCompletionSource? Blocked { get; init; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Expected asynchronous HTTP body reads");

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LargestRead = Math.Max(LargestRead, count);
        if (FailAfter is { } limit && BytesRead >= limit)
        {
            if (Blocked is { } blocked)
            {
                blocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            throw new IOException("DO-NOT-EXPOSE network-read-error");
        }
        var amount = (int)Math.Min(Math.Min(count, 8192), length - BytesRead);
        buffer.AsSpan(offset, amount).Fill(0x5a);
        BytesRead += amount;
        return amount;
    }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

internal sealed class DriveTargetStream : Stream
{
    public long BytesWritten { get; private set; }
    public bool Disposed { get; private set; }
    public Action? OnWrite { get; init; }
    public Exception? WriteException { get; init; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Expected asynchronous destination writes");
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (WriteException is { } exception) throw exception;
        Assert.True(buffer.Span.IndexOfAnyExcept((byte)0x5a) < 0);
        OnWrite?.Invoke();
        BytesWritten += buffer.Length;
        return ValueTask.CompletedTask;
    }
    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}
