using GoogleDriveDownloader.Application.Logging;

namespace GoogleDriveDownloader.Application.Storage;

public interface IFileStorageService
{
    // Повернення callback означає підтверджений успіх передачі; помилка має бути винятком.
    Task<string> SaveAsync(string directory, string fileName, Func<Stream, CancellationToken, Task> writeContent,
        LogContext? context = null, CancellationToken cancellationToken = default);
}
