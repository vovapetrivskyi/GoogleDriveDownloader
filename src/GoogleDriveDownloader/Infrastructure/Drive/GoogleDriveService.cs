using Google.Apis.Download;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using GoogleDriveDownloader.Application.Configuration;
using GoogleDriveDownloader.Application.Drive;
using GoogleDriveDownloader.Application.Input;

namespace GoogleDriveDownloader.Infrastructure.Drive;

public sealed class GoogleDriveService : IGoogleDriveService, IDisposable
{
    private const string MetadataFields = "id,name,mimeType,fileExtension,size,trashed,capabilities(canDownload)";
    private const long ExportLimit = 10 * 1024 * 1024;
    private readonly DriveService _drive;
    private readonly AppSettings _settings;

    public GoogleDriveService(AppSettings settings, IConfigurableHttpClientInitializer authentication,
        IHttpClientFactory? httpClientFactory = null)
    {
        _settings = settings;
        _drive = new DriveService(new BaseClientService.Initializer
        {
            ApplicationName = "GoogleDriveDownloader",
            HttpClientInitializer = new DriveHttpInitializer(authentication, settings.Download.RequestTimeoutSeconds),
            HttpClientFactory = httpClientFactory ?? new HttpClientFactory(),
            DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.None
        });
    }

    public Task<DriveFileMetadata> GetMetadataAsync(DriveLink link, CancellationToken cancellationToken = default) =>
        ExecuteAsync(link, async (context, token) =>
        {
            var request = _drive.Files.Get(link.FileId);
            request.Fields = MetadataFields;
            request.SupportsAllDrives = true;
            request.ModifyRequest += context.Apply;
            var file = await request.ExecuteAsync(token).ConfigureAwait(false);
            if (file is null || file.Id != link.FileId || string.IsNullOrWhiteSpace(file.Name) ||
                string.IsNullOrWhiteSpace(file.MimeType) || file.Trashed is null || file.Size is < 0)
                throw new DriveServiceException(DriveErrorCode.InvalidMetadata, "Google Drive повернув неповні або некоректні метадані файла.");
            return new DriveFileMetadata(file.Id, file.Name, file.MimeType, file.FileExtension, file.Size,
                file.Trashed.Value, file.Capabilities?.CanDownload);
        }, cancellationToken);

    public DriveTransferFormat GetTransferFormat(DriveFileMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata.MimeType) || metadata.Size is < 0)
            throw new DriveServiceException(DriveErrorCode.InvalidMetadata, "Метадані файла неповні або некоректні.");
        if (metadata.IsTrashed)
            throw new DriveServiceException(DriveErrorCode.Trashed, "Файл переміщено до кошика Google Drive.");
        if (metadata.MimeType == "application/vnd.google-apps.folder")
            throw new DriveServiceException(DriveErrorCode.UnsupportedType, "Посилання веде на папку. Потрібне посилання на окремий файл.");
        if (metadata.MimeType == "application/vnd.google-apps.shortcut")
            throw new DriveServiceException(DriveErrorCode.UnsupportedType, "Ярлики Google Drive не підтримуються. Використайте посилання на сам файл.");
        if (metadata.CanDownload != true)
            throw new DriveServiceException(DriveErrorCode.DownloadRestricted, metadata.CanDownload == false
                ? "Завантаження файла заборонено для поточного Google-акаунта."
                : "Google Drive не підтвердив дозвіл на завантаження файла.");
        return metadata.MimeType switch
        {
            "application/vnd.google-apps.document" => new(_settings.Export.DocsMimeType, ".docx"),
            "application/vnd.google-apps.spreadsheet" => new(_settings.Export.SheetsMimeType, ".xlsx"),
            "application/vnd.google-apps.presentation" => new(_settings.Export.SlidesMimeType, ".pptx"),
            var mime when mime.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal) =>
                throw new DriveServiceException(DriveErrorCode.UnsupportedType, "Цей тип Google Workspace не підтримується. Доступні Docs, Sheets і Slides."),
            _ => new(metadata.MimeType)
        };
    }

    public Task<long> DownloadAsync(DriveLink link, DriveFileMetadata metadata, Stream destination,
        CancellationToken cancellationToken = default) => ExecuteAsync(link, async (context, token) =>
    {
        if (metadata.FileId != link.FileId || string.IsNullOrWhiteSpace(metadata.MimeType) || metadata.Size is < 0)
            throw new DriveServiceException(DriveErrorCode.InvalidMetadata, "Метадані не відповідають запитаному файлу.");
        if (!destination.CanWrite)
            throw new DriveServiceException(DriveErrorCode.DestinationWriteFailed, "Цільовий потік недоступний для запису.");
        var format = GetTransferFormat(metadata);
        using var output = new DriveDestinationStream(destination, format.IsExport ? ExportLimit : null);
        IDownloadProgress progress;
        if (format.IsExport)
        {
            var request = _drive.Files.Export(link.FileId, format.MimeType);
            ConfigureDownloader(request.MediaDownloader, context);
            progress = await request.DownloadAsync(output, token).ConfigureAwait(false);
        }
        else
        {
            var request = _drive.Files.Get(link.FileId);
            request.SupportsAllDrives = true;
            ConfigureDownloader(request.MediaDownloader, context);
            progress = await request.DownloadAsync(output, token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        if (progress.Exception is { } exception) throw DriveErrorClassifier.Classify(exception, context);
        if (progress.Status != DownloadStatus.Completed || progress.BytesDownloaded != output.BytesWritten ||
            (!format.IsExport && metadata.Size is { } expected && expected != output.BytesWritten))
            throw new DriveServiceException(DriveErrorCode.IncompleteDownload, "Передачу файла завершено не повністю. Часткові дані не можна вважати готовим файлом.", isTransient: true);
        return output.BytesWritten;
    }, cancellationToken);

    private static void ConfigureDownloader(IMediaDownloader downloader, DriveRequestContext context)
    {
        downloader.ChunkSize = 1024 * 1024;
        // MediaDownloader створює власний HTTP request; ModifyRequest метаданих до нього не застосовується.
        ((MediaDownloader)downloader).ModifyRequest += context.Apply;
    }

    private async Task<T> ExecuteAsync<T>(DriveLink link, Func<DriveRequestContext, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateLink(link);
        var context = new DriveRequestContext(link);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // ResponseHeadersRead обмежує HttpClient.Timeout лише заголовками; цей token охоплює також тіло.
        timeout.CancelAfter(TimeSpan.FromSeconds(_settings.Download.RequestTimeoutSeconds));
        try
        {
            var result = await operation(context, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception)
        {
            // Не повертаємо сирі Google-помилки, відповіді чи URL з resource key до журналу/UI.
            throw DriveErrorClassifier.Classify(exception, context);
        }
    }

    private static void ValidateLink(DriveLink link)
    {
        if (string.IsNullOrEmpty(link.FileId) || link.FileId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-') ||
            (link.ResourceKey is { } key && (key.Length == 0 || key.Any(character => character is < '!' or > '~' or '/' or ',' or '\\'))))
            throw new DriveServiceException(DriveErrorCode.InvalidRequest, "Некоректний File ID або resource key.");
    }

    public void Dispose() => _drive.Dispose();

    private sealed class DriveHttpInitializer(IConfigurableHttpClientInitializer authentication, int timeoutSeconds) : IConfigurableHttpClientInitializer
    {
        public void Initialize(ConfigurableHttpClient httpClient)
        {
            httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            httpClient.MessageHandler.NumTries = 2;
            httpClient.MessageHandler.AddUnsuccessfulResponseHandler(new DriveRequestContext.ResponseHandler());
            authentication.Initialize(httpClient);
        }
    }
}
