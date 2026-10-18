using GoogleDriveDownloader.Application.Logging;
using GoogleDriveDownloader.Application.Storage;

namespace GoogleDriveDownloader.Infrastructure.Storage;

public class FileStorageService(FileNameResolver names, ILoggingService logging) : IFileStorageService
{
    public const int MaximumFullPathLength = 259;
    private const int PartNameLength = 42;
    private readonly SemaphoreSlim _commitGate = new(1, 1);

    public Task<string> SaveAsync(string directory, string fileName, Func<Stream, CancellationToken, Task> writeContent,
        LogContext? context = null, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(writeContent);
        FileNameResolver.ValidateFileName(fileName);
        string? partPath = null;
        try
        {
            var root = ResolveDirectory(directory);
            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            var nameBudget = Math.Min(FileNameResolver.MaximumFileNameLength, MaximumFullPathLength - prefix.Length);
            if (nameBudget < PartNameLength)
                throw new FileStorageException(FileStorageErrorCode.PathTooLong, "Шлях надто довгий для тимчасового файла. Оберіть коротшу директорію.");
            names.GetCandidate(fileName, maxLength: nameBudget);
            var temporary = CreateTemporary(root);
            partPath = temporary.Path;
            await using (var stream = temporary.Stream)
            {
                await writeContent(stream, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in Directory.EnumerateFileSystemEntries(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    occupied.Add(Path.GetFileName(entry).TrimEnd(' ', '.'));
                }
                for (long sequence = 1; sequence <= int.MaxValue; sequence++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var candidate = names.GetCandidate(fileName, (int)sequence, nameBudget);
                    if (occupied.Contains(candidate)) continue;
                    var finalPath = ChildPath(root, candidate);
                    try
                    {
                        // Остаточне перейменування в тій самій директорії є межею успішного завершення.
                        // Перевірка Exists сама по собі недостатня: Move завжди виконується без overwrite.
                        MoveFile(temporary.Path, finalPath);
                        partPath = null;
                        return finalPath;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        if (!IsOccupied(root, candidate)) throw;
                        occupied.Add(candidate);
                    }
                }
                throw new FileStorageException(FileStorageErrorCode.IoFailure, "Не вдалося підібрати вільне ім'я файла.");
            }
            finally { _commitGate.Release(); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            throw Classify(exception);
        }
        finally
        {
            if (partPath is not null)
            {
                try { DeletePartialFile(partPath); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Не маскуємо початкову помилку або скасування помилкою прибирання.
                    logging.Write(LogSeverity.Warning, "Не вдалося видалити незавершений файл «" + Path.GetFileName(partPath) + "». Видаліть його після відновлення доступу.",
                        (context ?? new LogContext()) with { Status = "PartialFileCleanupFailed" });
                }
            }
        }
    }, cancellationToken);

    protected virtual FileStream OpenTemporaryFile(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan, BufferSize = 64 * 1024
    });

    protected virtual void MoveFile(string source, string destination) => File.Move(source, destination, overwrite: false);

    protected virtual void DeletePartialFile(string path) => File.Delete(path);

    private (string Path, FileStream Stream) CreateTemporary(string root)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var path = ChildPath(root, ".gdd-" + Guid.NewGuid().ToString("N") + ".part");
            try { return (path, OpenTemporaryFile(path)); }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 80 or 183) { }
        }
        throw new FileStorageException(FileStorageErrorCode.IoFailure, "Не вдалося створити унікальний тимчасовий файл.");
    }

    private static string ResolveDirectory(string directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException();
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            if (root.StartsWith("\\\\?\\", StringComparison.Ordinal) || root.StartsWith("\\\\.\\", StringComparison.Ordinal) || !Directory.Exists(root))
                throw new FileStorageException(FileStorageErrorCode.DestinationUnavailable, "Директорія збереження недоступна або не є звичайною директорією.");
            return root;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new FileStorageException(FileStorageErrorCode.DestinationUnavailable, "Шлях до директорії збереження некоректний.");
        }
    }

    private static string ChildPath(string root, string name)
    {
        FileNameResolver.ValidateFileName(name);
        var result = Path.GetFullPath(Path.Combine(root, name));
        if (!string.Equals(Path.GetDirectoryName(result), root, StringComparison.OrdinalIgnoreCase))
            throw new FileStorageException(FileStorageErrorCode.InvalidName, "Ім'я файла виходить за межі директорії збереження.");
        return result;
    }

    private static bool IsOccupied(string root, string name) => Directory.EnumerateFileSystemEntries(root)
        .Any(path => string.Equals(Path.GetFileName(path).TrimEnd(' ', '.'), name, StringComparison.OrdinalIgnoreCase));

    private static FileStorageException Classify(Exception exception) => exception switch
    {
        PathTooLongException => new(FileStorageErrorCode.PathTooLong, "Шлях до файла надто довгий. Оберіть коротшу директорію."),
        IOException io when (io.HResult & 0xffff) is 0x70 or 0x27 => new(FileStorageErrorCode.DiskFull, "На диску недостатньо місця."),
        DirectoryNotFoundException or FileNotFoundException or UnauthorizedAccessException or ObjectDisposedException =>
            new(FileStorageErrorCode.DestinationUnavailable, "Директорія або потік збереження недоступні. Перевірте наявність директорії та права доступу."),
        _ => new(FileStorageErrorCode.IoFailure, "Не вдалося записати або перейменувати файл. Перевірте доступність директорії й диска.")
    };
}
