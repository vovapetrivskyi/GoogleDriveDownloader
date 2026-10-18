namespace GoogleDriveDownloader.Application.Logging;

public interface ILoggingService
{
    // Події надходять із фонового потоку; UI має передавати їх через IProgress<LogEntry>.
    event Action<LogEntry>? EntryLogged;

    void RegisterSensitiveValues(params string[] values);

    void Write(LogSeverity level, string message, LogContext? context = null, Exception? exception = null);
}
