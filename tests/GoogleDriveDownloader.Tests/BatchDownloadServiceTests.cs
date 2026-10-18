using GoogleDriveDownloader.Application.Batch;
using GoogleDriveDownloader.Application.Drive;
using GoogleDriveDownloader.Application.Input;
using GoogleDriveDownloader.Application.Storage;

namespace GoogleDriveDownloader.Tests;

public sealed class BatchDownloadServiceTests
{
    [Fact]
    public async Task InvalidAndInaccessibleRowsDoNotStopSubsequentRowsOrDoubleCount()
    {
        using var context = new BatchTestContext();
        var rows = BatchTestContext.Rows(4).Rows.ToArray();
        rows[1] = new InputRow(3, "", null, new[] { "Рядок 3: відсутня назва." });
        context.Drive.Metadata = (link, _, _) => link.FileId == "File_4"
            ? throw new DriveServiceException(DriveErrorCode.AccessDenied, "Доступ заборонено.", 403)
            : Task.FromResult(GoogleDriveServiceTests.Metadata() with { FileId = link.FileId });

        var result = await context.RunAsync(new ExcelReadResult(1, rows));

        Assert.Equal(BatchCompletionStatus.Completed, result.Status);
        Assert.StartsWith("Готово.", result.Message);
        Assert.Equal((4, 4, 2, 2, 0), (result.Total, result.Processed, result.Succeeded, result.Failed, result.Skipped));
        Assert.Equal(new[] { 2, 3, 4, 5 }, result.Results.Select(row => row.RowNumber));
        Assert.Equal(new[] { 1, 0, 1, 1 }, result.Results.Select(row => row.Attempts));
        Assert.Equal(3, context.Drive.MetadataCalls);
        Assert.Equal(2, context.Drive.DownloadCalls);
        Assert.Equal(2, Directory.GetFiles(context.Directory).Length);
        Assert.Empty(context.Retry.Delays);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 4 }, context.Progress.Values.Select(value => value.Processed));
        Assert.Equal(BatchCompletionStatus.Completed, context.Progress.Values[^1].CompletionStatus);
        var log = Assert.Single(context.Log.Entries, entry => entry.Context.RowNumber == 4 && entry.Context.Status == "Failed");
        Assert.Equal(("Документ 4", "File_4", 403), (log.Context.DocumentName, log.Context.FileId, log.Context.HttpStatusCode));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RetryBudgetIsBoundedAndExhaustionIsOneRowFailure(int retries)
    {
        using var context = new BatchTestContext(retries);
        context.Drive.Metadata = (_, _, _) => throw new DriveServiceException(
            DriveErrorCode.ServerError, "Тимчасова помилка сервера.", 503, isTransient: true);

        var result = await context.RunAsync(BatchTestContext.Rows(1));

        Assert.Equal(BatchCompletionStatus.Completed, result.Status);
        Assert.Equal((0, 1, 0), (result.Succeeded, result.Failed, result.Skipped));
        Assert.Equal(retries + 1, Assert.Single(result.Results).Attempts);
        Assert.Equal(retries + 1, context.Drive.MetadataCalls);
        Assert.Equal(0, context.Drive.DownloadCalls);
        Assert.Equal(retries, context.Retry.Delays.Count);
        Assert.Equal(retries, context.Log.Entries.Count(entry => entry.Context.Status == "Retrying"));
        Assert.Single(context.Log.Entries, entry => entry.Context.Status == "Failed");
        Assert.Empty(Directory.GetFiles(context.Directory));
    }

    [Fact]
    public async Task RetryRestartsEntirePipelineWithFreshEmptyPartAndOneFinalFile()
    {
        using var context = new BatchTestContext();
        var partialPaths = new List<string>();
        context.Drive.Metadata = (link, call, _) => call == 1
            ? throw new DriveServiceException(DriveErrorCode.RateLimited, "Ліміт частоти.", 429, true, TimeSpan.FromSeconds(8))
            : Task.FromResult(GoogleDriveServiceTests.Metadata() with { FileId = link.FileId });
        context.Drive.Download = async (stream, call, token) =>
        {
            Assert.Equal(0, stream.Length);
            Assert.Equal(0, stream.Position);
            partialPaths.Add(Assert.IsType<FileStream>(stream).Name);
            await stream.WriteAsync(call == 1 ? new byte[] { 9, 9 } : new byte[] { 1, 2, 3, 4 }, token);
            if (call == 1) throw new DriveServiceException(DriveErrorCode.NetworkFailure, "Мережевий збій.", isTransient: true);
            return 4;
        };
        context.Retry.OnDelay = (_, _) =>
        {
            Assert.Empty(Directory.GetFiles(context.Directory, "*.part"));
            return Task.CompletedTask;
        };

        var result = await context.RunAsync(BatchTestContext.Rows(1));

        Assert.Equal((1, 0, 0), (result.Succeeded, result.Failed, result.Skipped));
        var row = Assert.Single(result.Results);
        Assert.Equal(3, row.Attempts);
        Assert.Equal(3, context.Drive.MetadataCalls);
        Assert.Equal(2, partialPaths.Distinct().Count());
        Assert.Equal(new[] { TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(2.25) }, context.Retry.Delays);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(row.FilePath!));
        Assert.Single(Directory.GetFiles(context.Directory));
        Assert.Single(context.Log.Entries, entry => entry.Context.Status == "Succeeded");
    }

    [Fact]
    public async Task MetadataAndTransferShareOneRetryBudget()
    {
        using var context = new BatchTestContext();
        context.Drive.Metadata = (link, call, _) => call < 3
            ? throw new DriveServiceException(DriveErrorCode.Timeout, "Тайм-аут.", isTransient: true)
            : Task.FromResult(GoogleDriveServiceTests.Metadata() with { FileId = link.FileId });
        context.Drive.Download = (_, _, _) => throw new DriveServiceException(DriveErrorCode.NetworkFailure, "Мережа.", isTransient: true);

        var result = await context.RunAsync(BatchTestContext.Rows(1));

        Assert.Equal(4, Assert.Single(result.Results).Attempts);
        Assert.Equal(4, context.Drive.MetadataCalls);
        Assert.Equal(2, context.Drive.DownloadCalls);
        Assert.Equal(3, context.Retry.Delays.Count);
        Assert.Equal(1, result.Failed);
        Assert.Empty(Directory.GetFiles(context.Directory));
    }

    [Theory]
    [InlineData(DriveErrorCode.AuthenticationRequired)]
    [InlineData(DriveErrorCode.DiskFull)]
    [InlineData(DriveErrorCode.DestinationWriteFailed)]
    public async Task CriticalDriveFailureStopsAndSkipsUnprocessedRows(DriveErrorCode code)
    {
        using var context = new BatchTestContext();
        context.Drive.Download = async (stream, call, token) =>
        {
            await stream.WriteAsync(new byte[] { 1, 2 }, token);
            if (call == 2) throw new DriveServiceException(code, "Критична помилка.", isTransient: true);
            return 2;
        };

        var result = await context.RunAsync();

        Assert.Equal(BatchCompletionStatus.Failed, result.Status);
        Assert.Equal((3, 2, 1, 1, 1), (result.Total, result.Processed, result.Succeeded, result.Failed, result.Skipped));
        Assert.Equal(code.ToString(), result.Results[1].ErrorCode);
        Assert.Equal(0, result.Results[2].Attempts);
        Assert.Equal(2, context.Drive.MetadataCalls);
        Assert.Empty(context.Retry.Delays);
        Assert.Single(Directory.GetFiles(context.Directory));
        Assert.Empty(Directory.GetFiles(context.Directory, "*.part"));
        Assert.Equal(1, context.Progress.Values[^1].Skipped);
    }

    [Theory]
    [InlineData(FileStorageErrorCode.DiskFull)]
    [InlineData(FileStorageErrorCode.DestinationUnavailable)]
    [InlineData(FileStorageErrorCode.IoFailure)]
    [InlineData(FileStorageErrorCode.PathTooLong)]
    public async Task DestinationFailureStopsBatch(FileStorageErrorCode code)
    {
        using var context = new BatchTestContext(storage: log => new StorageTestService(log)
        {
            OpenException = new FileStorageException(code, "Недоступне сховище.")
        });
        var result = await context.RunAsync();
        Assert.Equal(BatchCompletionStatus.Failed, result.Status);
        Assert.Equal((0, 1, 2), (result.Succeeded, result.Failed, result.Skipped));
        Assert.Equal(1, context.Drive.MetadataCalls);
        Assert.Equal(0, context.Drive.DownloadCalls);
        Assert.Empty(Directory.GetFiles(context.Directory));
    }

    [Fact]
    public async Task MissingRealDirectoryStopsRatherThanFailingEveryRow()
    {
        using var context = new BatchTestContext();
        var result = await context.Service.RunAsync(BatchTestContext.Rows(), Path.Combine(context.Directory, "missing"));
        Assert.Equal(BatchCompletionStatus.Failed, result.Status);
        Assert.Equal((1, 2), (result.Failed, result.Skipped));
        Assert.Equal("DestinationUnavailable", result.Results[0].ErrorCode);
    }

    [Fact]
    public async Task CancellationDuringRetryPreservesSuccessAndSkipsCurrentAndRemaining()
    {
        using var context = new BatchTestContext();
        using var cancellation = new CancellationTokenSource();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Drive.Download = async (stream, call, token) =>
        {
            await stream.WriteAsync(new byte[] { 1, 2 }, token);
            if (call == 2) throw new DriveServiceException(DriveErrorCode.RateLimited, "Ліміт частоти.", 403, true);
            return 2;
        };
        context.Retry.OnDelay = async (_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            Assert.Empty(Directory.GetFiles(context.Directory, "*.part"));
            waiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        var pending = context.RunAsync(cancellationToken: cancellation.Token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(BatchCompletionStatus.Cancelled, result.Status);
        Assert.StartsWith("Скасовано.", result.Message);
        Assert.Equal((1, 0, 2), (result.Succeeded, result.Failed, result.Skipped));
        Assert.Equal(new[] { 1, 1, 0 }, result.Results.Select(row => row.Attempts));
        Assert.Equal(2, context.Drive.DownloadCalls);
        Assert.Single(context.Retry.Delays);
        Assert.Single(Directory.GetFiles(context.Directory));
        Assert.False(context.Service.IsRunning);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationReachesMetadataAndFileWriteAndCleansResources(bool duringWrite)
    {
        using var context = new BatchTestContext();
        using var cancellation = new CancellationTokenSource();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Stream? target = null;
        if (duringWrite)
            context.Drive.Download = async (stream, _, token) =>
            {
                target = stream;
                Assert.Equal(cancellation.Token, token);
                await stream.WriteAsync(new byte[] { 1, 2 }, token);
                blocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 2;
            };
        else
            context.Drive.Metadata = async (_, _, token) =>
            {
                Assert.Equal(cancellation.Token, token);
                blocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return GoogleDriveServiceTests.Metadata();
            };
        var pending = context.RunAsync(cancellationToken: cancellation.Token);
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(BatchCompletionStatus.Cancelled, result.Status);
        Assert.Equal((0, 0, 3), (result.Succeeded, result.Failed, result.Skipped));
        Assert.Empty(Directory.GetFiles(context.Directory));
        if (target is not null) Assert.False(target.CanWrite);
        Assert.False(context.Service.IsRunning);
    }

    [Fact]
    public async Task CancellationAtCommitKeepsCommittedFileSuccessful()
    {
        using var cancellation = new CancellationTokenSource();
        using var context = new BatchTestContext(storage: log => new StorageTestService(log)
        {
            BeforeMove = (_, _) => cancellation.Cancel()
        });
        var result = await context.RunAsync(cancellationToken: cancellation.Token);
        Assert.Equal(BatchCompletionStatus.Cancelled, result.Status);
        Assert.Equal((1, 0, 2), (result.Succeeded, result.Failed, result.Skipped));
        Assert.True(File.Exists(result.Results[0].FilePath));
        Assert.Single(Directory.GetFiles(context.Directory));
    }

    [Fact]
    public async Task PreCancellationSkipsEveryKnownRowWithoutStartingDrive()
    {
        using var context = new BatchTestContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await context.RunAsync(cancellationToken: cancellation.Token);
        Assert.Equal(BatchCompletionStatus.Cancelled, result.Status);
        Assert.Equal((0, 0, 3), (result.Succeeded, result.Failed, result.Skipped));
        Assert.Equal(0, context.Drive.MetadataCalls);
        Assert.False(context.Service.IsRunning);
    }

    [Fact]
    public async Task SequentialTransfersAndConcurrentRunGuardAreReleasedAfterCompletion()
    {
        using var context = new BatchTestContext();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var peak = 0;
        context.Drive.Download = async (stream, call, token) =>
        {
            peak = Math.Max(peak, Interlocked.Increment(ref active));
            try
            {
                if (call == 1)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
                await stream.WriteAsync(new byte[] { 1 }, token);
                await Task.Yield();
                return 1;
            }
            finally { Interlocked.Decrement(ref active); }
        };
        var pending = context.RunAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(context.Service.IsRunning);
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.RunAsync());
            Assert.Equal(1, context.Drive.MetadataCalls);
        }
        finally { release.TrySetResult(); }
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, peak);
        Assert.Equal(3, result.Succeeded);
        Assert.False(context.Service.IsRunning);
        var second = await context.RunAsync(BatchTestContext.Rows(1));
        Assert.Equal(1, second.Succeeded);
        Assert.EndsWith("Документ 2 (2).pdf", second.Results[0].FilePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedFailuresAreSafeAndGuardAllowsRestart(bool duringDelay)
    {
        using var context = new BatchTestContext();
        if (duringDelay)
        {
            context.Drive.Metadata = (_, _, _) => throw new DriveServiceException(DriveErrorCode.Timeout, "Тайм-аут.", isTransient: true);
            context.Retry.OnDelay = (_, _) => throw new InvalidOperationException("DO-NOT-EXPOSE private-token");
        }
        else context.Drive.Format = _ => throw new InvalidOperationException("DO-NOT-EXPOSE private-token");

        var result = await context.RunAsync();

        Assert.Equal(BatchCompletionStatus.Failed, result.Status);
        Assert.Equal((1, 2), (result.Failed, result.Skipped));
        Assert.Equal("UnexpectedFailure", result.Results[0].ErrorCode);
        Assert.DoesNotContain("DO-NOT-EXPOSE", result.ToString());
        Assert.All(context.Log.Entries, entry =>
        {
            Assert.DoesNotContain("DO-NOT-EXPOSE", entry.Message);
            Assert.Null(entry.ExceptionDetails);
        });
        Assert.False(context.Service.IsRunning);
        Assert.Equal(BatchCompletionStatus.Completed, (await context.RunAsync(BatchTestContext.Rows(0))).Status);
    }

    [Fact]
    public async Task EmptyBatchCompletesAndBrokenProgressObserverDoesNotLoseCommittedResult()
    {
        using var context = new BatchTestContext();
        var empty = await context.RunAsync(BatchTestContext.Rows(0));
        Assert.Equal(BatchCompletionStatus.Completed, empty.Status);
        Assert.Equal(0, empty.Total);
        var result = await context.Service.RunAsync(BatchTestContext.Rows(1), context.Directory, new BrokenProgress());
        Assert.Equal(1, result.Succeeded);
        Assert.True(File.Exists(result.Results[0].FilePath));
        Assert.Contains(context.Log.Entries, entry => entry.Context.Status == "ProgressUnavailable");
    }

    private sealed class BrokenProgress : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => throw new InvalidOperationException("DO-NOT-EXPOSE observer-detail");
    }
}
