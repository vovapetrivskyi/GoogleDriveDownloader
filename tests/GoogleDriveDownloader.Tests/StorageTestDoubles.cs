using System.Collections.Concurrent;
using GoogleDriveDownloader.Application.Logging;
using GoogleDriveDownloader.Application.Storage;
using GoogleDriveDownloader.Infrastructure.Storage;

namespace GoogleDriveDownloader.Tests;

internal sealed class StorageTestLog : ILoggingService
{
    public ConcurrentQueue<LogEntry> Entries { get; } = new();
    public event Action<LogEntry>? EntryLogged;
    public void RegisterSensitiveValues(params string[] values) { }
    public void Write(LogSeverity level, string message, LogContext? context = null, Exception? exception = null)
    {
        var entry = new LogEntry(DateTimeOffset.UtcNow, level, message, context ?? new LogContext(), exception?.ToString());
        Entries.Enqueue(entry);
        EntryLogged?.Invoke(entry);
    }
}

internal sealed class StorageTestService(StorageTestLog log) : FileStorageService(new FileNameResolver(), log)
{
    public Action<string, string>? BeforeMove { get; set; }
    public Action<string, int>? BeforeOpen { get; set; }
    public Exception? MoveException { get; set; }
    public Exception? OpenException { get; set; }
    public bool FailCleanup { get; set; }
    public int MoveCalls { get; private set; }
    public int OpenCalls { get; private set; }

    protected override void MoveFile(string source, string destination)
    {
        MoveCalls++;
        BeforeMove?.Invoke(source, destination);
        if (MoveException is { } exception) throw exception;
        base.MoveFile(source, destination);
    }
    protected override FileStream OpenTemporaryFile(string path)
    {
        OpenCalls++;
        BeforeOpen?.Invoke(path, OpenCalls);
        if (OpenException is { } exception) throw exception;
        return base.OpenTemporaryFile(path);
    }
    protected override void DeletePartialFile(string path)
    {
        if (FailCleanup) throw new IOException("DO-NOT-EXPOSE cleanup-details");
        base.DeletePartialFile(path);
    }
}
