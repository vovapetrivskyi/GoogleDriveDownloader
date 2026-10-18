using GoogleDriveDownloader.Application.Batch;
using GoogleDriveDownloader.Application.Configuration;
using GoogleDriveDownloader.Application.Drive;
using GoogleDriveDownloader.Application.Input;
using GoogleDriveDownloader.Application.Storage;
using GoogleDriveDownloader.Infrastructure.Storage;

namespace GoogleDriveDownloader.Tests;

internal sealed class BatchTestDrive : IGoogleDriveService
{
    public int MetadataCalls { get; private set; }
    public int DownloadCalls { get; private set; }
    public Func<DriveLink, int, CancellationToken, Task<DriveFileMetadata>> Metadata { get; set; } =
        (link, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(GoogleDriveServiceTests.Metadata() with { FileId = link.FileId });
        };
    public Func<DriveFileMetadata, DriveTransferFormat> Format { get; set; } = _ => new("application/pdf");
    public Func<Stream, int, CancellationToken, Task<long>> Download { get; set; } = async (stream, _, token) =>
    {
        await stream.WriteAsync(new byte[] { 1, 2, 3, 4 }, token);
        return 4;
    };
    public Task<DriveFileMetadata> GetMetadataAsync(DriveLink link, CancellationToken cancellationToken = default) =>
        Metadata(link, ++MetadataCalls, cancellationToken);
    public DriveTransferFormat GetTransferFormat(DriveFileMetadata metadata) => Format(metadata);
    public Task<long> DownloadAsync(DriveLink link, DriveFileMetadata metadata, Stream destination,
        CancellationToken cancellationToken = default) => Download(destination, ++DownloadCalls, cancellationToken);
}

internal sealed class BatchTestRetry(int maximumRetries = 3) : DownloadRetryPolicy(new AppSettings
{
    Download = new DownloadSettings { RetryCount = maximumRetries }
})
{
    public List<TimeSpan> Delays { get; } = [];
    public Func<TimeSpan, CancellationToken, Task>? OnDelay { get; set; }
    protected override double NextJitter() => 0.25;
    protected override Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Delays.Add(delay);
        cancellationToken.ThrowIfCancellationRequested();
        return OnDelay?.Invoke(delay, cancellationToken) ?? Task.CompletedTask;
    }
}

internal sealed class BatchTestProgress : IProgress<DownloadProgress>
{
    public List<DownloadProgress> Values { get; } = [];
    public void Report(DownloadProgress value) => Values.Add(value);
}

internal sealed class BatchTestContext : IDisposable
{
    private readonly TestDirectory _directory = new();
    public string Directory => _directory.Path;
    public StorageTestLog Log { get; } = new();
    public BatchTestDrive Drive { get; } = new();
    public BatchTestRetry Retry { get; }
    public BatchTestProgress Progress { get; } = new();
    public BatchDownloadService Service { get; }

    public BatchTestContext(int retries = 3, Func<StorageTestLog, IFileStorageService>? storage = null)
    {
        Retry = new BatchTestRetry(retries);
        Service = new BatchDownloadService(new ExcelReader(new DriveLinkParser()), Drive, new FileNameResolver(),
            storage?.Invoke(Log) ?? new FileStorageService(new FileNameResolver(), Log), Retry, Log);
    }

    public static ExcelReadResult Rows(int count = 3) => new(1, Enumerable.Range(2, count)
        .Select(row => new InputRow(row, "Документ " + row, new DriveLink("File_" + row), Array.Empty<string>())).ToArray());

    public Task<BatchDownloadResult> RunAsync(ExcelReadResult? workbook = null, CancellationToken cancellationToken = default) =>
        Service.RunAsync(workbook ?? Rows(), Directory, Progress, cancellationToken);
    public void Dispose() => _directory.Dispose();
}
