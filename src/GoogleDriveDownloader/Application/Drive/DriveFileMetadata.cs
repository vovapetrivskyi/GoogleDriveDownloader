namespace GoogleDriveDownloader.Application.Drive;

public sealed record DriveFileMetadata(string FileId, string Name, string MimeType, string? FileExtension,
    long? Size, bool IsTrashed, bool? CanDownload);

public sealed record DriveTransferFormat(string MimeType, string? ExportExtension = null)
{
    public bool IsExport => ExportExtension is not null;
}
