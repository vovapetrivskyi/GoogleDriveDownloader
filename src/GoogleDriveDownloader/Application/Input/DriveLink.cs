namespace GoogleDriveDownloader.Application.Input;

public sealed record DriveLink(string FileId, string? ResourceKey = null);
