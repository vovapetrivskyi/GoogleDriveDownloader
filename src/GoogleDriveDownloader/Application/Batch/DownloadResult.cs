namespace GoogleDriveDownloader.Application.Batch;

public enum DownloadOutcome { Succeeded, Failed, Skipped }
public enum BatchCompletionStatus { Completed, Cancelled, Failed }

public sealed record DownloadResult(int RowNumber, string DocumentName, DownloadOutcome Outcome,
    string Message, int Attempts = 0, string? FilePath = null, string? FileId = null,
    string? ErrorCode = null, int? HttpStatusCode = null);

public sealed record BatchDownloadResult(BatchCompletionStatus Status, IReadOnlyList<DownloadResult> Results, string Message)
{
    public int Total => Results.Count;
    public int Succeeded => Results.Count(result => result.Outcome == DownloadOutcome.Succeeded);
    public int Failed => Results.Count(result => result.Outcome == DownloadOutcome.Failed);
    public int Skipped => Results.Count(result => result.Outcome == DownloadOutcome.Skipped);
    public int Processed => Succeeded + Failed;
}
