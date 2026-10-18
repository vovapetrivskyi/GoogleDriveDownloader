using GoogleDriveDownloader.Application.Logging;

namespace GoogleDriveDownloader.Application.Configuration;

public sealed class AppSettings
{
    public GoogleOAuthSettings GoogleOAuth { get; init; } = new();
    public DownloadSettings Download { get; init; } = new();
    public ExportSettings Export { get; init; } = new();
    public LoggingSettings Logging { get; init; } = new();
}

public sealed class GoogleOAuthSettings
{
    public const string DriveReadonlyScope = "https://www.googleapis.com/auth/drive.readonly";

    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";
    public string Scope { get; init; } = DriveReadonlyScope;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

public sealed class DownloadSettings
{
    public int MaxConcurrentDownloads { get; init; } = 1;
    public int RetryCount { get; init; } = 3;
    public int RequestTimeoutSeconds { get; init; } = 100;
    public int AuthorizationTimeoutSeconds { get; init; } = 300;
    public string DuplicatePolicy { get; init; } = "Rename";
}

public sealed class ExportSettings
{
    public const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string Pptx = "application/vnd.openxmlformats-officedocument.presentationml.presentation";

    public string DocsMimeType { get; init; } = Docx;
    public string SheetsMimeType { get; init; } = Xlsx;
    public string SlidesMimeType { get; init; } = Pptx;
}

public sealed class LoggingSettings
{
    public LogSeverity MinimumLevel { get; init; } = LogSeverity.Information;
    public int RetentionDays { get; init; } = 30;
}
