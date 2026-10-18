using GoogleDriveDownloader.Application.Drive;
using GoogleDriveDownloader.Application.Input;
using GoogleDriveDownloader.Infrastructure.Authentication;
using GoogleDriveDownloader.Infrastructure.Drive;
using static GoogleDriveDownloader.Tests.GoogleDriveServiceTests;

namespace GoogleDriveDownloader.Tests;

public sealed class GoogleDriveStreamingTests
{
    private static readonly DriveLink Link = new("File_1", "0-Resource_Key");

    [Fact]
    public async Task LargeBinaryStreamsWithBoundedBufferToNonSeekableDestination()
    {
        const long length = 32L * 1024 * 1024 + 7;
        using var source = new DriveSourceStream(length);
        using var transport = ContentTransport(source);
        using var service = Create(transport);
        var observed = false;
        using var target = new DriveTargetStream
        {
            OnWrite = () =>
            {
                if (observed) return;
                observed = true;
                Assert.True(source.BytesRead < length, "The first write must happen before the full body is read");
            }
        };
        Assert.Equal(length, await service.DownloadAsync(Link, Metadata(size: length), target));
        Assert.True(observed);
        Assert.InRange(source.LargestRead, 1, 1024 * 1024 + 1);
        Assert.Equal(length, target.BytesWritten);
        Assert.True(source.Disposed);
        Assert.False(target.Disposed);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task SuccessfulHttpResponseAboveExportLimitIsRejectedLocally()
    {
        using var source = new DriveSourceStream(11L * 1024 * 1024);
        using var transport = ContentTransport(source);
        using var service = Create(transport);
        using var target = new DriveTargetStream();
        var exception = await Assert.ThrowsAsync<DriveServiceException>(() => service.DownloadAsync(Link,
            Metadata("application/vnd.google-apps.document", size: null), target));
        Assert.Equal(DriveErrorCode.ExportTooLarge, exception.Code);
        Assert.InRange(target.BytesWritten, 1, 10L * 1024 * 1024);
        Assert.True(source.Disposed);
        Assert.False(target.Disposed);
    }

    [Fact]
    public async Task FailedBodyReadReturnsClassifiedErrorInsteadOfSdkFailedProgress()
    {
        using var source = new DriveSourceStream(5L * 1024 * 1024) { FailAfter = 1024 * 1024 + 1 };
        using var transport = ContentTransport(source);
        using var service = Create(transport);
        using var target = new DriveTargetStream();
        var exception = await Assert.ThrowsAsync<DriveServiceException>(() => service.DownloadAsync(Link, Metadata(size: 5L * 1024 * 1024), target));
        Assert.Equal(DriveErrorCode.NetworkFailure, exception.Code);
        Assert.True(exception.IsTransient);
        Assert.Equal(1024 * 1024, target.BytesWritten);
        Assert.True(source.Disposed);
        Assert.False(target.Disposed);
        AssertSafe(exception);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DestinationIoFailureIsNotMisclassifiedAsRetryableNetworkFailure(bool diskFull)
    {
        using var source = new DriveSourceStream(4);
        using var transport = ContentTransport(source);
        using var service = Create(transport);
        using var target = new DriveTargetStream
        {
            WriteException = new IOException("DO-NOT-EXPOSE private-output-path", diskFull ? unchecked((int)0x80070070) : unchecked((int)0x80070005))
        };
        var exception = await Assert.ThrowsAsync<DriveServiceException>(() => service.DownloadAsync(Link, Metadata(), target));
        Assert.Equal(diskFull ? DriveErrorCode.DiskFull : DriveErrorCode.DestinationWriteFailed, exception.Code);
        Assert.False(exception.IsTransient);
        Assert.True(source.Disposed);
        Assert.False(target.Disposed);
        AssertSafe(exception);
    }

    [Fact]
    public async Task CancellationDuringBodyReadPropagatesCancellationAndReleasesResponse()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = new DriveSourceStream(5L * 1024 * 1024) { FailAfter = 1024 * 1024 + 1, Blocked = blocked };
        using var transport = ContentTransport(source);
        using var service = Create(transport);
        using var target = new DriveTargetStream();
        using var cancellation = new CancellationTokenSource();
        var operation = service.DownloadAsync(Link, Metadata(size: null), target, cancellation.Token);
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(1024 * 1024, target.BytesWritten);
        Assert.True(source.Disposed);
        Assert.False(target.Disposed);
    }

    [Fact]
    public async Task TimeoutCoversBodyAfterHeadersHaveAlreadyArrived()
    {
        using var source = new DriveSourceStream(4) { FailAfter = 0, Blocked = new TaskCompletionSource() };
        using var transport = ContentTransport(source);
        using var service = Create(transport, timeout: 1);
        using var target = new DriveTargetStream();
        var exception = await Assert.ThrowsAsync<DriveServiceException>(() => service.DownloadAsync(Link, Metadata(), target));
        Assert.Equal(DriveErrorCode.Timeout, exception.Code);
        Assert.True(exception.IsTransient);
        Assert.True(source.Disposed);
        Assert.False(target.Disposed);
    }

    [Fact]
    public async Task MetadataTimeoutAndNetworkFailuresAreSanitized()
    {
        using var transport = new DriveTestTransport
        {
            RespondAsync = async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("Unreachable");
            }
        };
        using var service = Create(transport, timeout: 1);
        var exception = await Assert.ThrowsAsync<DriveServiceException>(() => service.GetMetadataAsync(Link));
        Assert.Equal(DriveErrorCode.Timeout, exception.Code);
        Assert.Single(transport.Requests);
        transport.RespondAsync = (_, _) => throw new HttpRequestException("DO-NOT-EXPOSE network-details");
        exception = await Assert.ThrowsAsync<DriveServiceException>(() => service.GetMetadataAsync(Link));
        Assert.Equal(DriveErrorCode.NetworkFailure, exception.Code);
        AssertSafe(exception);
    }

    [Fact]
    public async Task PreCancelledOperationsDoNotIssueHttpRequests()
    {
        using var transport = new DriveTestTransport();
        using var service = Create(transport);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetMetadataAsync(Link, cancellation.Token));
        using var target = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAsync(Link, Metadata(), target, cancellation.Token));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task RealAuthInitializerRefreshesOnceOnDrive401UsingSeparateFakeTransports()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        using var oauth = new OAuthTestTransport();
        await using var auth = new GoogleAuthService(scope.Settings, scope.Configuration, scope.Logging, () => new OAuthTestReceiver(), oauth);
        await auth.SignInAsync();
        using var transport = new DriveTestTransport();
        transport.RespondAsync = (_, _) => Task.FromResult(transport.Requests.Count == 1
            ? DriveTestTransport.Error(System.Net.HttpStatusCode.Unauthorized) : DriveTestTransport.Metadata());
        using var service = new GoogleDriveService(scope.Settings, auth, transport);
        var metadata = await service.GetMetadataAsync(Link);
        Assert.Equal("File_1", metadata.FileId);
        Assert.Equal(2, transport.Requests.Count);
        Assert.Equal(2, oauth.Requests.Count);
        Assert.All(transport.Requests, request => Assert.Equal("Bearer synthetic-access-new", request.Authorization));
    }

    [Fact]
    public async Task UnauthenticatedDriveCallNeverReachesNetworkAndReturnsSafeContractError()
    {
        await using var scope = new AuthTestScope();
        await using var auth = new GoogleAuthService(scope.Settings, scope.Configuration, scope.Logging);
        using var transport = new DriveTestTransport();
        using var service = new GoogleDriveService(scope.Settings, auth, transport);
        var exception = await Assert.ThrowsAsync<DriveServiceException>(() => service.GetMetadataAsync(Link));
        Assert.Equal(DriveErrorCode.AuthenticationRequired, exception.Code);
        Assert.Empty(transport.Requests);
        AssertSafe(exception);
    }

    [Fact]
    public async Task RepeatedDrive401ClearsSessionAtSdkRetryLimitAndRequestsNewSignIn()
    {
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        using var oauth = new OAuthTestTransport();
        await using var auth = new GoogleAuthService(scope.Settings, scope.Configuration, scope.Logging, () => new OAuthTestReceiver(), oauth);
        await auth.SignInAsync();
        using var transport = new DriveTestTransport
        {
            RespondAsync = (_, _) => Task.FromResult(DriveTestTransport.Error(System.Net.HttpStatusCode.Unauthorized))
        };
        using var service = new GoogleDriveService(scope.Settings, auth, transport);
        var exception = await Assert.ThrowsAsync<DriveServiceException>(() => service.GetMetadataAsync(Link));
        Assert.Equal(DriveErrorCode.AuthenticationRequired, exception.Code);
        Assert.Equal(401, exception.HttpStatusCode);
        Assert.Equal(AuthenticationStatus.AuthorizationRequired, auth.Status);
        Assert.False(File.Exists(scope.TokenPath));
        Assert.Equal(2, transport.Requests.Count);
        Assert.Equal(2, oauth.Requests.Count);
        AssertSafe(exception);
    }
    private static DriveTestTransport ContentTransport(Stream source) => new()
    {
        RespondAsync = (_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(source) })
    };
}
