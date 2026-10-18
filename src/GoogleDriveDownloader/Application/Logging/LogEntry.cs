namespace GoogleDriveDownloader.Application.Logging;

public enum LogSeverity
{
    Information,
    Warning,
    Error
}

public sealed record LogContext(
    int? RowNumber = null,
    string? DocumentName = null,
    string? FileId = null,
    string? Status = null,
    int? HttpStatusCode = null);

public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogSeverity Level,
    string Message,
    LogContext Context,
    string? ExceptionDetails = null);
