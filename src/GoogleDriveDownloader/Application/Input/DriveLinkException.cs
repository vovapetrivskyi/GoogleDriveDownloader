namespace GoogleDriveDownloader.Application.Input;

public sealed class DriveLinkException(string message) : Exception(message);
