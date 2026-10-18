namespace GoogleDriveDownloader.Application.Storage;

public enum FileStorageErrorCode
{
    InvalidName,
    PathTooLong,
    DestinationUnavailable,
    DiskFull,
    IoFailure
}

public sealed class FileStorageException(FileStorageErrorCode code, string message) : Exception(message)
{
    public FileStorageErrorCode Code { get; } = code;
}
