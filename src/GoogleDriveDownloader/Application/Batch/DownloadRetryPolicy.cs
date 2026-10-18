using GoogleDriveDownloader.Application.Configuration;
using GoogleDriveDownloader.Application.Drive;

namespace GoogleDriveDownloader.Application.Batch;

public class DownloadRetryPolicy
{
    public int MaximumRetries { get; }

    public DownloadRetryPolicy(AppSettings settings)
    {
        if (settings.Download.RetryCount is < 0 or > 3 || settings.Download.MaxConcurrentDownloads != 1)
            throw new ArgumentException("MVP підтримує одне завантаження й від 0 до 3 повторів.");
        MaximumRetries = settings.Download.RetryCount;
    }

    public bool CanRetry(DriveServiceException exception) => exception.IsTransient && exception.Code is
        DriveErrorCode.RateLimited or DriveErrorCode.ServerError or DriveErrorCode.NetworkFailure or
        DriveErrorCode.Timeout or DriveErrorCode.IncompleteDownload;

    public TimeSpan GetDelay(int retryNumber, TimeSpan? retryAfter = null)
    {
        if (retryNumber is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(retryNumber));
        var backoff = TimeSpan.FromSeconds(Math.Pow(2, retryNumber - 1) + NextJitter());
        return retryAfter is { } requested && requested > backoff ? requested : backoff;
    }

    public async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        // Retry-After може перевищити межу одного Task.Delay; не скорочуємо вимогу сервера.
        while (delay > TimeSpan.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = delay > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay;
            await DelayAsync(chunk, cancellationToken).ConfigureAwait(false);
            delay -= chunk;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    protected virtual double NextJitter() => Random.Shared.NextDouble();
    protected virtual Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}
