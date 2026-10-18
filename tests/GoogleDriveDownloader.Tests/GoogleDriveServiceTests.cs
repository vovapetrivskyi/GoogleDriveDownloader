using System.Net;
using System.Net.Http.Headers;
using GoogleDriveDownloader.Application.Configuration;
using GoogleDriveDownloader.Application.Drive;
using GoogleDriveDownloader.Application.Input;
using GoogleDriveDownloader.Infrastructure.Authentication;
using GoogleDriveDownloader.Infrastructure.Drive;

namespace GoogleDriveDownloader.Tests;

public sealed class GoogleDriveServiceTests
{
    private static readonly DriveLink Link = new("File_1", "0-Resource_Key");

    [Fact]
    public async Task MetadataRequestsExplicitFieldsSharedDrivesAndPerRequestResourceKey()
    {
        using var transport = new DriveTestTransport();
        using var service = Create(transport);
        var metadata = await service.GetMetadataAsync(Link);
        Assert.Equal(new DriveFileMetadata("File_1", "original.pdf", "application/pdf", "pdf", 4, false, true), metadata);
        var request = Assert.Single(transport.Requests);
        Assert.Equal("/drive/v3/files/File_1", request.Uri.AbsolutePath);
        var query = OAuthTestTransport.Parse(request.Uri.Query);
        Assert.Equal("true", query["supportsAllDrives"]);
        Assert.Equal("id,name,mimeType,fileExtension,size,trashed,capabilities(canDownload)", query["fields"]);
        Assert.False(query.ContainsKey("resourcekey"));
        Assert.Equal("File_1/0-Resource_Key", request.ResourceKeys);
        Assert.Equal("Bearer synthetic-drive-access", request.Authorization);
        Assert.Equal(TimeSpan.FromSeconds(100), Assert.Single(transport.Clients).Timeout);
    }

    [Fact]
    public async Task ResourceKeysDoNotLeakBetweenConcurrentMetadataRequests()
    {
        using var transport = new DriveTestTransport
        {
            RespondAsync = async (request, _) =>
            {
                await Task.Yield();
                return DriveTestTransport.Metadata(id: request.RequestUri!.Segments.Last());
            }
        };
        using var service = Create(transport);
        await Task.WhenAll(service.GetMetadataAsync(new DriveLink("First", "key-A")),
            service.GetMetadataAsync(new DriveLink("Second", "key-B")), service.GetMetadataAsync(new DriveLink("Third")));
        foreach (var request in transport.Requests)
        {
            Assert.Equal(request.Uri.Segments.Last() switch { "First" => "First/key-A", "Second" => "Second/key-B", _ => null }, request.ResourceKeys);
        }
    }

    [Fact]
    public async Task BinaryDownloadStreamsOriginalBytesAndLeavesDestinationOpen()
    {
        byte[] bytes = [1, 2, 3, 4];
        using var transport = new DriveTestTransport { RespondAsync = (_, _) => Task.FromResult(DriveTestTransport.Content(bytes)) };
        using var service = Create(transport);
        using var output = new MemoryStream();
        Assert.Equal(bytes.Length, await service.DownloadAsync(Link, Metadata(), output));
        Assert.Equal(bytes, output.ToArray());
        Assert.True(output.CanWrite);
        var request = Assert.Single(transport.Requests);
        var query = OAuthTestTransport.Parse(request.Uri.Query);
        Assert.Equal("media", query["alt"]);
        Assert.Equal("true", query["supportsAllDrives"]);
        Assert.False(query.ContainsKey("mimeType"));
        Assert.Equal("File_1/0-Resource_Key", request.ResourceKeys);
        Assert.False(service.GetTransferFormat(Metadata()).IsExport);
    }

    [Theory]
    [InlineData("application/vnd.google-apps.document", ExportSettings.Docx, ".docx")]
    [InlineData("application/vnd.google-apps.spreadsheet", ExportSettings.Xlsx, ".xlsx")]
    [InlineData("application/vnd.google-apps.presentation", ExportSettings.Pptx, ".pptx")]
    public async Task WorkspaceExportUsesConfiguredOfficeMimeAndResourceKey(string sourceMime, string targetMime, string extension)
    {
        using var transport = new DriveTestTransport { RespondAsync = (_, _) => Task.FromResult(DriveTestTransport.Content([1, 2, 3])) };
        using var service = Create(transport);
        var metadata = Metadata(mime: sourceMime, size: 9999);
        var format = service.GetTransferFormat(metadata);
        Assert.Equal(new DriveTransferFormat(targetMime, extension), format);
        using var output = new MemoryStream();
        Assert.Equal(3, await service.DownloadAsync(Link, metadata, output));
        var request = Assert.Single(transport.Requests);
        Assert.Equal("/drive/v3/files/File_1/export", request.Uri.AbsolutePath);
        var query = OAuthTestTransport.Parse(request.Uri.Query);
        Assert.Equal(targetMime, query["mimeType"]);
        Assert.False(query.ContainsKey("supportsAllDrives"));
        Assert.Equal("File_1/0-Resource_Key", request.ResourceKeys);
        Assert.True(output.CanWrite);
    }

    [Theory]
    [InlineData("application/vnd.google-apps.folder")]
    [InlineData("application/vnd.google-apps.shortcut")]
    [InlineData("application/vnd.google-apps.form")]
    [InlineData("application/vnd.google-apps.drawing")]
    [InlineData("application/vnd.google-apps.vid")]
    public async Task UnsupportedTypesNeverDownloadOrFollowShortcuts(string mime)
    {
        using var transport = new DriveTestTransport();
        using var service = Create(transport);
        using var output = new MemoryStream();
        var exception = await Assert.ThrowsAsync<DriveServiceException>(() => service.DownloadAsync(Link, Metadata(mime), output));
        Assert.Equal(DriveErrorCode.UnsupportedType, exception.Code);
        Assert.False(exception.IsTransient);
        Assert.Empty(transport.Requests);
        Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData(false, true, DriveErrorCode.Trashed)]
    [InlineData(false, false, DriveErrorCode.DownloadRestricted)]
    [InlineData(null, false, DriveErrorCode.DownloadRestricted)]
    public async Task TrashedOrUnconfirmedDownloadPermissionIsRejectedBeforeContentRequest(bool? canDownload, bool trashed, DriveErrorCode code)
    {
        using var transport = new DriveTestTransport();
        using var service = Create(transport);
        using var output = new MemoryStream();
        var exception = await Assert.ThrowsAsync<DriveServiceException>(() => service.DownloadAsync(Link,
            Metadata() with { CanDownload = canDownload, IsTrashed = trashed }, output));
        Assert.Equal(code, exception.Code);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task MissingCapabilityStaysUnknownAndFailsClosedForTransfer()
    {
        using var transport = new DriveTestTransport { RespondAsync = (_, _) => Task.FromResult(DriveTestTransport.Metadata(canDownload: null)) };
        using var service = Create(transport);
        var metadata = await service.GetMetadataAsync(Link);
        Assert.Null(metadata.CanDownload);
        Assert.Equal(DriveErrorCode.DownloadRestricted, Assert.Throws<DriveServiceException>(() => service.GetTransferFormat(metadata)).Code);
    }

    [Theory]
    [InlineData(401, "authError", DriveErrorCode.AuthenticationRequired, false)]
    [InlineData(403, "forbidden", DriveErrorCode.AccessDenied, false)]
    [InlineData(403, "insufficientFilePermissions", DriveErrorCode.AccessDenied, false)]
    [InlineData(404, "notFound", DriveErrorCode.NotFoundOrInaccessible, false)]
    [InlineData(403, "rateLimitExceeded", DriveErrorCode.RateLimited, true)]
    [InlineData(403, "userRateLimitExceeded", DriveErrorCode.RateLimited, true)]
    [InlineData(429, "rateLimitExceeded", DriveErrorCode.RateLimited, true)]
    [InlineData(500, "backendError", DriveErrorCode.ServerError, true)]
    [InlineData(502, "backendError", DriveErrorCode.ServerError, true)]
    [InlineData(503, "backendError", DriveErrorCode.ServerError, true)]
    [InlineData(504, "backendError", DriveErrorCode.ServerError, true)]
    [InlineData(403, "downloadRestricted", DriveErrorCode.DownloadRestricted, false)]
    [InlineData(403, "fileNotDownloadable", DriveErrorCode.UnsupportedType, false)]
    [InlineData(403, "fileNotExportable", DriveErrorCode.UnsupportedType, false)]
    [InlineData(400, "badRequest", DriveErrorCode.ApiError, false)]
    public async Task ClassifiesMetadataErrorsWithoutAutomaticRetriesOrSensitiveDetails(int status, string reason, DriveErrorCode code, bool transient)
    {
        using var transport = new DriveTestTransport { RespondAsync = (_, _) => Task.FromResult(DriveTestTransport.Error((HttpStatusCode)status, reason, 30)) };
        using var service = Create(transport);
        var exception = await Assert.ThrowsAsync<DriveServiceException>(() => service.GetMetadataAsync(Link));
        Assert.Equal(code, exception.Code);
        Assert.Equal(status, exception.HttpStatusCode);
        Assert.Equal(transient, exception.IsTransient);
        Assert.Equal(TimeSpan.FromSeconds(30), exception.RetryAfter);
        Assert.Single(transport.Requests);
        AssertSafe(exception);
    }

    [Theory]
    [InlineData(403, "forbidden", DriveErrorCode.AccessDenied)]
    [InlineData(404, "notFound", DriveErrorCode.NotFoundOrInaccessible)]
    [InlineData(403, "exportSizeLimitExceeded", DriveErrorCode.ExportTooLarge)]
    [InlineData(400, "exportSizeLimitExceeded", DriveErrorCode.ExportTooLarge)]
    public async Task FailedSdkDownloadProgressIsNeverReturnedAsSuccess(int status, string reason, DriveErrorCode code)
    {
        using var transport = new DriveTestTransport { RespondAsync = (_, _) => Task.FromResult(DriveTestTransport.Error((HttpStatusCode)status, reason)) };
        using var service = Create(transport);
        using var output = new MemoryStream();
        var exception = await Assert.ThrowsAsync<DriveServiceException>(() => service.DownloadAsync(Link,
            Metadata("application/vnd.google-apps.document"), output));
        Assert.Equal(code, exception.Code);
        Assert.Equal(status, exception.HttpStatusCode);
        Assert.False(exception.IsTransient);
        Assert.Equal(0, output.Length);
        Assert.Single(transport.Requests);
        AssertSafe(exception);
    }

    [Fact]
    public async Task RetryAfterHttpDateIsPreservedForFutureBatchPolicy()
    {
        using var transport = new DriveTestTransport
        {
            RespondAsync = (_, _) =>
            {
                var response = DriveTestTransport.Error(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(1));
                return Task.FromResult(response);
            }
        };
        using var service = Create(transport);
        var exception = await Assert.ThrowsAsync<DriveServiceException>(() => service.GetMetadataAsync(Link));
        Assert.InRange(exception.RetryAfter!.Value.TotalSeconds, 55, 61);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(503)]
    public async Task NonJsonHttpErrorPreservesStatusWithoutEchoingBody(int status)
    {
        using var transport = new DriveTestTransport
        {
            RespondAsync = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent("DO-NOT-EXPOSE malformed private error") })
        };
        using var service = Create(transport);
        var exception = await Assert.ThrowsAsync<DriveServiceException>(() => service.GetMetadataAsync(Link));
        Assert.Equal(status, exception.HttpStatusCode);
        AssertSafe(exception);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task PartialBinaryResponseWithCompletedSdkStatusStillFailsLengthCheck()
    {
        using var transport = new DriveTestTransport { RespondAsync = (_, _) => Task.FromResult(DriveTestTransport.Content([1, 2])) };
        using var service = Create(transport);
        using var output = new MemoryStream();
        var exception = await Assert.ThrowsAsync<DriveServiceException>(() => service.DownloadAsync(Link, Metadata(size: 4), output));
        Assert.Equal(DriveErrorCode.IncompleteDownload, exception.Code);
        Assert.True(exception.IsTransient);
        Assert.Equal(2, output.Length);
        Assert.True(output.CanWrite);
    }

    [Fact]
    public async Task EmptyBinaryFileIsValid()
    {
        using var transport = new DriveTestTransport { RespondAsync = (_, _) => Task.FromResult(DriveTestTransport.Content([])) };
        using var service = Create(transport);
        using var output = new MemoryStream();
        Assert.Equal(0, await service.DownloadAsync(Link, Metadata(size: 0), output));
    }

    [Theory]
    [InlineData("OtherId", "application/pdf", 4)]
    [InlineData("File_1", "", 4)]
    [InlineData("File_1", "application/pdf", -1)]
    public async Task InvalidMetadataIsRejected(string id, string mime, long size)
    {
        using var transport = new DriveTestTransport { RespondAsync = (_, _) => Task.FromResult(DriveTestTransport.Metadata(id: id, mime: mime, size: size)) };
        using var service = Create(transport);
        var exception = await Assert.ThrowsAsync<DriveServiceException>(() => service.GetMetadataAsync(Link));
        Assert.Equal(DriveErrorCode.InvalidMetadata, exception.Code);
        AssertSafe(exception);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad/id")]
    [InlineData("id\r\nInjected")]
    public async Task InvalidFileIdNeverIssuesRequest(string id)
    {
        using var transport = new DriveTestTransport();
        using var service = Create(transport);
        var exception = await Assert.ThrowsAsync<DriveServiceException>(() => service.GetMetadataAsync(new DriveLink(id)));
        Assert.Equal(DriveErrorCode.InvalidRequest, exception.Code);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task DownloadRejectsMismatchedMetadataAndReadOnlyDestinationBeforeHttp()
    {
        using var transport = new DriveTestTransport();
        using var service = Create(transport);
        using var output = new MemoryStream();
        Assert.Equal(DriveErrorCode.InvalidMetadata, (await Assert.ThrowsAsync<DriveServiceException>(() =>
            service.DownloadAsync(Link, Metadata() with { FileId = "OtherId" }, output))).Code);
        using var readOnly = new MemoryStream([1, 2], writable: false);
        Assert.Equal(DriveErrorCode.DestinationWriteFailed, (await Assert.ThrowsAsync<DriveServiceException>(() =>
            service.DownloadAsync(Link, Metadata(), readOnly))).Code);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task DisposingAdapterDisposesOwnedSdkHttpClient()
    {
        using var transport = new DriveTestTransport();
        var service = Create(transport);
        await service.GetMetadataAsync(Link);
        service.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Assert.Single(transport.Clients).GetAsync("https://www.googleapis.com/drive/v3/files"));
    }

    internal static GoogleDriveService Create(DriveTestTransport transport, int timeout = 100) =>
        new(new AppSettings { Download = new DownloadSettings { RequestTimeoutSeconds = timeout } }, new DriveTestTransport.Authentication(), transport);

    internal static DriveFileMetadata Metadata(string mime = "application/pdf", long? size = 4) =>
        new("File_1", "original.pdf", mime, "pdf", size, false, true);

    internal static void AssertSafe(DriveServiceException exception)
    {
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("DO-NOT-EXPOSE", exception.ToString());
        Assert.DoesNotContain("Resource_Key", exception.ToString());
        Assert.DoesNotContain("synthetic-drive-access", exception.ToString());
    }
}
