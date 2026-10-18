namespace GoogleDriveDownloader.Application.Batch;

// Processed = Succeeded + Failed; після зупинки решта рядків потрапляє до Skipped.
public sealed record DownloadProgress(int Total, int Processed, int Succeeded, int Failed, int Skipped,
    int? CurrentRowNumber = null, BatchCompletionStatus? CompletionStatus = null);
