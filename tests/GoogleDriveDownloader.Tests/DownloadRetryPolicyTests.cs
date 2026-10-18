using GoogleDriveDownloader.Application.Batch;
using GoogleDriveDownloader.Application.Configuration;
using GoogleDriveDownloader.Application.Drive;

namespace GoogleDriveDownloader.Tests;

public sealed class DownloadRetryPolicyTests
{
    [Fact]
    public void BackoffIncludesJitterAndHonorsLargerRetryAfter()
    {
        var policy = new BatchTestRetry();
        Assert.Equal(new[] { 1.25, 2.25, 4.25 }, Enumerable.Range(1, 3).Select(n => policy.GetDelay(n).TotalSeconds));
        Assert.Equal(TimeSpan.FromMinutes(2), policy.GetDelay(2, TimeSpan.FromMinutes(2)));
        Assert.Equal(TimeSpan.FromSeconds(4.25), policy.GetDelay(3, TimeSpan.FromSeconds(-10)));
        Assert.Equal(TimeSpan.FromSeconds(2.25), policy.GetDelay(2, TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(DriveErrorCode.RateLimited, true)]
    [InlineData(DriveErrorCode.ServerError, true)]
    [InlineData(DriveErrorCode.NetworkFailure, true)]
    [InlineData(DriveErrorCode.Timeout, true)]
    [InlineData(DriveErrorCode.IncompleteDownload, true)]
    [InlineData(DriveErrorCode.AccessDenied, false)]
    [InlineData(DriveErrorCode.NotFoundOrInaccessible, false)]
    [InlineData(DriveErrorCode.AuthenticationRequired, false)]
    [InlineData(DriveErrorCode.DiskFull, false)]
    [InlineData(DriveErrorCode.DestinationWriteFailed, false)]
    [InlineData(DriveErrorCode.InvalidRequest, false)]
    [InlineData(DriveErrorCode.ExportTooLarge, false)]
    public void RetryRequiresBothTransientFlagAndEligibleError(DriveErrorCode code, bool expected)
    {
        var policy = new BatchTestRetry();
        Assert.Equal(expected, policy.CanRetry(new DriveServiceException(code, "safe", isTransient: true)));
        Assert.False(policy.CanRetry(new DriveServiceException(code, "safe", isTransient: false)));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(4, 1)]
    [InlineData(3, 2)]
    public void UnsupportedSettingsAreRejected(int retryCount, int concurrent) =>
        Assert.Throws<ArgumentException>(() => new DownloadRetryPolicy(new AppSettings
        {
            Download = new DownloadSettings { RetryCount = retryCount, MaxConcurrentDownloads = concurrent }
        }));

    [Fact]
    public async Task LongServerDelayIsChunkedWithoutShortening()
    {
        var policy = new BatchTestRetry();
        await policy.WaitAsync(TimeSpan.FromDays(2) + TimeSpan.FromSeconds(3), CancellationToken.None);
        Assert.Equal(new[] { TimeSpan.FromDays(1), TimeSpan.FromDays(1), TimeSpan.FromSeconds(3) }, policy.Delays);
    }

    [Fact]
    public async Task ProductionDelayCanBeCancelledImmediately()
    {
        var policy = new DownloadRetryPolicy(new AppSettings());
        using var cancellation = new CancellationTokenSource();
        var pending = policy.WaitAsync(TimeSpan.FromDays(10), cancellation.Token);
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
