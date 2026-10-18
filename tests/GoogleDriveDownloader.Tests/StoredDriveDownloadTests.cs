using System.Net;
using GoogleDriveDownloader.Application.Drive;
using GoogleDriveDownloader.Application.Input;
using GoogleDriveDownloader.Application.Storage;
using GoogleDriveDownloader.Infrastructure.Storage;

namespace GoogleDriveDownloader.Tests;

public sealed class StoredDriveDownloadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealSdkDownloadEitherCommitsCompleteContentOrCleansFailedProgress(bool fail)
    {
        using var directory = new TestDirectory();
        using var transport = new DriveTestTransport
        {
            RespondAsync = (request, _) => Task.FromResult(request.RequestUri!.Query.Contains("alt=media", StringComparison.Ordinal)
                ? fail ? DriveTestTransport.Error(HttpStatusCode.Forbidden) : DriveTestTransport.Content([1, 2, 3, 4])
                : DriveTestTransport.Metadata())
        };
        using var drive = GoogleDriveServiceTests.Create(transport);
        var link = new DriveLink("File_1");
        var metadata = await drive.GetMetadataAsync(link);
        var names = new FileNameResolver();
        var fileName = names.Resolve("Звіт за 2025 рік.txt", metadata, drive.GetTransferFormat(metadata));
        var storage = new FileStorageService(names, new StorageTestLog());
        if (fail)
        {
            var exception = await Assert.ThrowsAsync<DriveServiceException>(() => storage.SaveAsync(directory.Path, fileName,
                (stream, token) => drive.DownloadAsync(link, metadata, stream, token)));
            Assert.Equal(DriveErrorCode.AccessDenied, exception.Code);
            Assert.Empty(Directory.GetFiles(directory.Path));
        }
        else
        {
            var final = await storage.SaveAsync(directory.Path, fileName, (stream, token) => drive.DownloadAsync(link, metadata, stream, token));
            Assert.Equal("Звіт за 2025 рік.pdf", Path.GetFileName(final));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(final));
            Assert.Empty(Directory.GetFiles(directory.Path, "*.part"));
        }
    }

    [Fact]
    public async Task SdkCompletedButTruncatedResponseCannotBecomeFinalFile()
    {
        using var directory = new TestDirectory();
        using var transport = new DriveTestTransport { RespondAsync = (_, _) => Task.FromResult(DriveTestTransport.Content([1, 2])) };
        using var drive = GoogleDriveServiceTests.Create(transport);
        var metadata = GoogleDriveServiceTests.Metadata(size: 4);
        var storage = new FileStorageService(new FileNameResolver(), new StorageTestLog());
        var exception = await Assert.ThrowsAsync<DriveServiceException>(() => storage.SaveAsync(directory.Path, "report.pdf",
            (stream, token) => drive.DownloadAsync(new DriveLink("File_1"), metadata, stream, token)));
        Assert.Equal(DriveErrorCode.IncompleteDownload, exception.Code);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }
}
