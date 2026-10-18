namespace GoogleDriveDownloader.Application.Drive;

public enum DriveErrorCode
{
    InvalidRequest,
    InvalidMetadata,
    AuthenticationRequired,
    AccessDenied,
    NotFoundOrInaccessible,
    DownloadRestricted,
    Trashed,
    UnsupportedType,
    ExportTooLarge,
    RateLimited,
    ServerError,
    NetworkFailure,
    Timeout,
    DestinationWriteFailed,
    DiskFull,
    IncompleteDownload,
    ApiError
}

public sealed class DriveServiceException(DriveErrorCode code, string message, int? httpStatusCode = null,
    bool isTransient = false, TimeSpan? retryAfter = null) : Exception(message)
{
    public DriveErrorCode Code { get; } = code;
    public int? HttpStatusCode { get; } = httpStatusCode;
    public bool IsTransient { get; } = isTransient;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
