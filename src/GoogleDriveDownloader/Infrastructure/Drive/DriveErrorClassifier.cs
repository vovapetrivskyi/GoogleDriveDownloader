using Google;
using GoogleDriveDownloader.Application.Drive;
using GoogleDriveDownloader.Infrastructure.Authentication;

namespace GoogleDriveDownloader.Infrastructure.Drive;

internal static class DriveErrorClassifier
{
    public static DriveServiceException Classify(Exception exception, DriveRequestContext context)
    {
        if (exception is DriveServiceException known) return known;
        if (exception is AuthenticationException or AuthStorageException)
            return new(DriveErrorCode.AuthenticationRequired, "Потрібна повторна авторизація через Google.", context.HttpStatusCode);
        if (exception is OperationCanceledException)
            return new(DriveErrorCode.Timeout, "Час очікування запиту Google Drive вичерпано.", isTransient: true);
        if (exception is GoogleApiException api)
        {
            var status = (int)api.HttpStatusCode;
            if (status == 0) status = context.HttpStatusCode ?? api.Error?.Code ?? 0;
            var reasons = api.Error?.Errors?.Select(error => error.Reason).ToHashSet(StringComparer.Ordinal) ?? [];
            DriveServiceException Error(DriveErrorCode code, string message, bool transient = false) =>
                new(code, message, status == 0 ? null : status, transient, context.RetryAfter);

            if (status == 401)
                return Error(DriveErrorCode.AuthenticationRequired, "Google відхилив авторизацію. Увійдіть повторно.");
            if (reasons.Contains("exportSizeLimitExceeded"))
                return Error(DriveErrorCode.ExportTooLarge, "Експорт Google Workspace перевищує обмеження files.export у 10 МБ.");
            if (status == 429 || (status == 403 && reasons.Overlaps(["rateLimitExceeded", "userRateLimitExceeded"])))
                return Error(DriveErrorCode.RateLimited, "Google Drive тимчасово обмежив частоту запитів.", true);
            if (status is 500 or 502 or 503 or 504)
                return Error(DriveErrorCode.ServerError, "Тимчасова помилка сервера Google Drive.", true);
            if (status == 404)
                return Error(DriveErrorCode.NotFoundOrInaccessible, "Файл не знайдено або він недоступний поточному Google-акаунту.");
            if (reasons.Overlaps(["cannotDownloadFile", "downloadRestricted", "download_restricted_for_revision"]))
                return Error(DriveErrorCode.DownloadRestricted, "Завантаження цього файла заборонено.");
            if (reasons.Overlaps(["fileNotDownloadable", "fileNotExportable"]))
                return Error(DriveErrorCode.UnsupportedType, "Google Drive не підтримує цей спосіб завантаження або експорту файла.");
            if (status == 403)
                return Error(DriveErrorCode.AccessDenied, "Google Drive заборонив доступ. Перевірте дозволи на файл і політики Google-акаунта.");
            return Error(DriveErrorCode.ApiError, "Google Drive не зміг виконати запит.");
        }
        if (exception is HttpRequestException or IOException)
            return new(DriveErrorCode.NetworkFailure, "Не вдалося отримати дані Google Drive. Перевірте мережу.", isTransient: true);
        return new(DriveErrorCode.ApiError, "Не вдалося обробити відповідь Google Drive.");
    }
}
