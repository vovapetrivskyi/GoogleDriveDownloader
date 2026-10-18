using GoogleDriveDownloader.Application.Drive;
using GoogleDriveDownloader.Application.Input;
using GoogleDriveDownloader.Application.Logging;
using GoogleDriveDownloader.Application.Storage;

namespace GoogleDriveDownloader.Application.Batch;

public sealed class BatchDownloadService(ExcelReader excel, IGoogleDriveService drive, FileNameResolver names,
    IFileStorageService storage, DownloadRetryPolicy retry, ILoggingService logging)
{
    private int _running;
    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public Task<BatchDownloadResult> RunAsync(string workbookPath, string directory,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default) =>
        StartAsync(token => excel.ReadAsync(workbookPath, token), directory, progress, cancellationToken);

    public Task<BatchDownloadResult> RunAsync(ExcelReadResult workbook, string directory,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default) =>
        StartAsync(_ => Task.FromResult(workbook), directory, progress, cancellationToken);

    private Task<BatchDownloadResult> StartAsync(Func<CancellationToken, Task<ExcelReadResult>> load, string directory,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("Пакетне завантаження вже виконується.");
        // Навіть за попереднього скасування повертаємо підсумок і звільняємо захист повторного запуску.
        return Task.Run(async () =>
        {
            try { return await RunCoreAsync(load, directory, progress, cancellationToken).ConfigureAwait(false); }
            finally { Volatile.Write(ref _running, 0); }
        });
    }

    private async Task<BatchDownloadResult> RunCoreAsync(Func<CancellationToken, Task<ExcelReadResult>> load, string directory,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        InputRow[] rows;
        try
        {
            rows = (await load(cancellationToken).ConfigureAwait(false)).Rows.ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Finish(BatchCompletionStatus.Cancelled, [], "Скасовано до завершення читання XLSX.", progress);
        }
        catch (Exception exception)
        {
            var message = exception is ExcelReadException ? exception.Message : "Не вдалося підготувати вхідні дані пакета.";
            return Finish(BatchCompletionStatus.Failed, [], message, progress);
        }

        var results = new List<DownloadResult>(rows.Length);
        var succeeded = 0;
        var failed = 0;
        var skipped = 0;
        var status = BatchCompletionStatus.Completed;
        string? stopMessage = null;
        logging.Write(LogSeverity.Information, $"Початок обробки. Знайдено {rows.Length} документів.", new LogContext(Status: "BatchStarted"));
        Report(progress, new DownloadProgress(rows.Length, 0, 0, 0, 0));

        for (var index = 0; index < rows.Length; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                status = BatchCompletionStatus.Cancelled;
                stopMessage = "Скасовано користувачем.";
                break;
            }
            var row = rows[index];
            var execution = await ProcessRowAsync(row, directory, cancellationToken).ConfigureAwait(false);
            results.Add(execution.Result);
            switch (execution.Result.Outcome)
            {
                case DownloadOutcome.Succeeded: succeeded++; break;
                case DownloadOutcome.Failed: failed++; break;
                case DownloadOutcome.Skipped: skipped++; break;
            }
            LogResult(execution.Result);
            Report(progress, new DownloadProgress(rows.Length, succeeded + failed, succeeded, failed, skipped, row.RowNumber));
            if (execution.Stop is { } stop)
            {
                status = stop;
                stopMessage = execution.Result.Message;
                break;
            }
        }
        if (status == BatchCompletionStatus.Completed && cancellationToken.IsCancellationRequested)
            status = BatchCompletionStatus.Cancelled;

        // Кожний запис має рівно один результат. Поточний скасований запис уже додано, решта — ще ні.
        for (var index = results.Count; index < rows.Length; index++)
        {
            var row = rows[index];
            var result = new DownloadResult(row.RowNumber, row.DocumentName, DownloadOutcome.Skipped,
                "Пропущено після зупинки пакета.", FileId: row.Link?.FileId);
            results.Add(result);
            LogResult(result);
        }
        return Finish(status, results.AsReadOnly(), stopMessage, progress);
    }

    private async Task<RowExecution> ProcessRowAsync(InputRow row, string directory, CancellationToken cancellationToken)
    {
        if (!row.IsValid || string.IsNullOrWhiteSpace(row.DocumentName))
            return new(new DownloadResult(row.RowNumber, row.DocumentName, DownloadOutcome.Failed,
                row.Errors.Count != 0 ? string.Join(" ", row.Errors) : "Рядок містить порожню назву або некоректне посилання.",
                FileId: row.Link?.FileId, ErrorCode: "ValidationFailed"));

        for (var attempt = 1; ; attempt++)
        {
            var saving = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var metadata = await drive.GetMetadataAsync(row.Link!, cancellationToken).ConfigureAwait(false);
                var format = drive.GetTransferFormat(metadata);
                var fileName = names.Resolve(row.DocumentName, metadata, format);
                cancellationToken.ThrowIfCancellationRequested();
                saving = true;
                var path = await storage.SaveAsync(directory, fileName,
                    (stream, token) => drive.DownloadAsync(row.Link!, metadata, stream, token),
                    Context(row, "Downloading"), cancellationToken).ConfigureAwait(false);
                // SaveAsync уже виконав commit. Пізнє скасування не може зробити готовий файл пропущеним.
                return new(new DownloadResult(row.RowNumber, row.DocumentName, DownloadOutcome.Succeeded,
                    "Файл збережено.", attempt, path, row.Link!.FileId));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Cancelled(row, attempt);
            }
            catch (DriveServiceException exception) when (retry.CanRetry(exception) && attempt <= retry.MaximumRetries)
            {
                try
                {
                    var delay = retry.GetDelay(attempt, exception.RetryAfter);
                    logging.Write(LogSeverity.Warning,
                        $"Тимчасова помилка. Повтор {attempt}/{retry.MaximumRetries} через {delay.TotalSeconds:F2} с.",
                        Context(row, "Retrying", exception.HttpStatusCode));
                    await retry.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Cancelled(row, attempt); }
                catch (Exception delayException) { return Failed(row, attempt, Describe(delayException, saving)); }
            }
            catch (Exception exception)
            {
                return Failed(row, attempt, Describe(exception, saving));
            }
        }
    }

    private static RowExecution Cancelled(InputRow row, int attempt) => new(
        new DownloadResult(row.RowNumber, row.DocumentName, DownloadOutcome.Skipped, "Скасовано під час обробки рядка.",
            attempt, FileId: row.Link?.FileId), BatchCompletionStatus.Cancelled);

    private static RowExecution Failed(InputRow row, int attempt, Failure failure) => new(
        new DownloadResult(row.RowNumber, row.DocumentName, DownloadOutcome.Failed,
            failure.Message, attempt, FileId: row.Link?.FileId, ErrorCode: failure.Code, HttpStatusCode: failure.HttpStatusCode),
        failure.Critical ? BatchCompletionStatus.Failed : null);

    private static Failure Describe(Exception exception, bool saving) => exception switch
    {
        DriveServiceException drive => new(drive.Code.ToString(), drive.Message, drive.HttpStatusCode,
            drive.Code is DriveErrorCode.AuthenticationRequired or DriveErrorCode.DiskFull or DriveErrorCode.DestinationWriteFailed),
        FileStorageException file => new(file.Code.ToString(), file.Message, null,
            file.Code is FileStorageErrorCode.DiskFull or FileStorageErrorCode.DestinationUnavailable or FileStorageErrorCode.IoFailure ||
            (saving && file.Code == FileStorageErrorCode.PathTooLong)),
        _ => new("UnexpectedFailure", "Непередбачена помилка обробки. Пакет зупинено.", null, true)
    };

    private BatchDownloadResult Finish(BatchCompletionStatus status, IReadOnlyList<DownloadResult> results,
        string? detail, IProgress<DownloadProgress>? progress)
    {
        var summary = new BatchDownloadResult(status, results, "");
        var prefix = status switch
        {
            BatchCompletionStatus.Completed => "Готово.",
            BatchCompletionStatus.Cancelled => "Скасовано.",
            _ => "Пакет зупинено через критичну помилку."
        };
        var message = $"{prefix} Успішно: {summary.Succeeded}, помилок: {summary.Failed}, пропущено: {summary.Skipped}.";
        if (status == BatchCompletionStatus.Failed && detail is not null) message += " " + detail;
        summary = summary with { Message = message };
        logging.Write(status == BatchCompletionStatus.Failed ? LogSeverity.Error : LogSeverity.Information,
            message, new LogContext(Status: "Batch" + status));
        Report(progress, new DownloadProgress(summary.Total, summary.Processed, summary.Succeeded,
            summary.Failed, summary.Skipped, CompletionStatus: status));
        return summary;
    }

    private void LogResult(DownloadResult result)
    {
        var message = result.Outcome switch
        {
            DownloadOutcome.Succeeded => "Завантажено: " + Path.GetFileName(result.FilePath),
            DownloadOutcome.Failed => "Помилка: " + result.Message,
            _ => result.Message
        };
        // Сирі винятки адаптерів можуть містити приватні URL/токени; до журналу й результату вони не потрапляють.
        logging.Write(result.Outcome == DownloadOutcome.Failed ? LogSeverity.Error : LogSeverity.Information, message,
            new LogContext(result.RowNumber, result.DocumentName, result.FileId, result.Outcome.ToString(), result.HttpStatusCode));
    }

    private void Report(IProgress<DownloadProgress>? progress, DownloadProgress value)
    {
        try { progress?.Report(value); }
        catch (Exception)
        {
            logging.Write(LogSeverity.Warning, "Не вдалося передати оновлення прогресу.", new LogContext(Status: "ProgressUnavailable"));
        }
    }

    private static LogContext Context(InputRow row, string status, int? httpStatusCode = null) =>
        new(row.RowNumber, row.DocumentName, row.Link?.FileId, status, httpStatusCode);
    private sealed record RowExecution(DownloadResult Result, BatchCompletionStatus? Stop = null);
    private sealed record Failure(string Code, string Message, int? HttpStatusCode, bool Critical);
}
