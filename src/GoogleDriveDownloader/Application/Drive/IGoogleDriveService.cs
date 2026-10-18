using GoogleDriveDownloader.Application.Input;

namespace GoogleDriveDownloader.Application.Drive;

public interface IGoogleDriveService
{
    Task<DriveFileMetadata> GetMetadataAsync(DriveLink link, CancellationToken cancellationToken = default);
    DriveTransferFormat GetTransferFormat(DriveFileMetadata metadata);
    Task<long> DownloadAsync(DriveLink link, DriveFileMetadata metadata, Stream destination,
        CancellationToken cancellationToken = default);
}
