using System.Threading.Channels;
using GoogleDriveDownloader.Application.Configuration;
using GoogleDriveDownloader.Application.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Json;
using Serilog.Parsing;

namespace GoogleDriveDownloader.Infrastructure.Logging;

public sealed class LoggingService : ILoggingService, IAsyncDisposable
{
    private static readonly MessageTemplate Template = new MessageTemplateParser().Parse("{Message:l}");
    private readonly Channel<LogEntry> _entries = Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false
    });
    private readonly LoggingSettings _settings;
    private readonly SensitiveDataRedactor _redactor;
    private readonly string? _userDataDirectory;
    private readonly Task _worker;
    private int _fileFailed;

    public LoggingService(AppSettings settings, string? userDataDirectory = null)
    {
        _settings = settings.Logging;
        _redactor = new SensitiveDataRedactor(settings.GoogleOAuth.ClientId, settings.GoogleOAuth.ClientSecret);
        _userDataDirectory = userDataDirectory;
        _worker = Task.Run(ProcessEntriesAsync);
    }

    public event Action<LogEntry>? EntryLogged;

    public void RegisterSensitiveValues(params string[] values) => _redactor.Register(values);

    public void Write(LogSeverity level, string message, LogContext? context = null, Exception? exception = null)
    {
        if (level < _settings.MinimumLevel)
        {
            return;
        }

        context ??= new LogContext();
        var safeContext = context with
        {
            DocumentName = RedactOptional(context.DocumentName),
            FileId = RedactOptional(context.FileId),
            Status = RedactOptional(context.Status)
        };
        var entry = new LogEntry(DateTimeOffset.Now, level, _redactor.Redact(message), safeContext,
            exception is null ? null : _redactor.Redact(exception.ToString()));
        _entries.Writer.TryWrite(entry);
    }

    public ValueTask DisposeAsync()
    {
        _entries.Writer.TryComplete();
        return new ValueTask(_worker);
    }

    private async Task ProcessEntriesAsync()
    {
        Logger? logger = null;
        var failurePublished = false;
        try
        {
            try
            {
                var dataDirectory = _userDataDirectory;
                if (dataDirectory is null)
                {
                    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    if (string.IsNullOrWhiteSpace(localAppData))
                    {
                        throw new IOException("LocalApplicationData unavailable.");
                    }

                    dataDirectory = Path.Combine(localAppData, "GoogleDriveDownloader");
                }

                logger = new LoggerConfiguration()
                    .MinimumLevel.Information()
                    .WriteTo.Fallible(sink => sink.File(
                        new JsonFormatter(renderMessage: true),
                        Path.Combine(dataDirectory, "logs", "log-.jsonl"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: null,
                        retainedFileTimeLimit: TimeSpan.FromDays(_settings.RetentionDays),
                        fileSizeLimitBytes: null,
                        shared: true),
                        new FileFailureListener(() => Interlocked.Exchange(ref _fileFailed, 1)))
                    .CreateLogger();
            }
            catch (Exception)
            {
                Interlocked.Exchange(ref _fileFailed, 1);
            }

            await foreach (var entry in _entries.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (Volatile.Read(ref _fileFailed) == 0)
                {
                    try
                    {
                        logger?.Write(ToSerilogEvent(entry));
                    }
                    catch (Exception)
                    {
                        Interlocked.Exchange(ref _fileFailed, 1);
                    }
                }

                Publish(entry);
                if (!failurePublished && Volatile.Read(ref _fileFailed) != 0)
                {
                    failurePublished = true;
                    // Не передаємо діагностику sink: вона може містити довільний шлях чи вміст події.
                    Publish(new LogEntry(DateTimeOffset.Now, LogSeverity.Warning,
                        "Файловий журнал недоступний або неповний. Повідомлення залишаються доступними в інтерфейсі; перевірте права та вільне місце й перезапустіть програму.",
                        new LogContext(Status: "FileLoggingUnavailable")));
                }
            }
        }
        finally
        {
            try
            {
                if (logger is not null)
                {
                    await logger.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                Publish(new LogEntry(DateTimeOffset.Now, LogSeverity.Warning,
                    "Не вдалося завершити запис файлового журналу.", new LogContext(Status: "FileLoggingUnavailable")));
            }
        }
    }

    private static LogEvent ToSerilogEvent(LogEntry entry) => new(
        entry.Timestamp,
        entry.Level switch
        {
            LogSeverity.Warning => LogEventLevel.Warning,
            LogSeverity.Error => LogEventLevel.Error,
            _ => LogEventLevel.Information
        },
        exception: null,
        Template,
        [
            new LogEventProperty("Message", new ScalarValue(entry.Message)),
            new LogEventProperty("RowNumber", new ScalarValue(entry.Context.RowNumber)),
            new LogEventProperty("DocumentName", new ScalarValue(entry.Context.DocumentName)),
            new LogEventProperty("FileId", new ScalarValue(entry.Context.FileId)),
            new LogEventProperty("Status", new ScalarValue(entry.Context.Status)),
            new LogEventProperty("HttpStatusCode", new ScalarValue(entry.Context.HttpStatusCode)),
            new LogEventProperty("ExceptionDetails", new ScalarValue(entry.ExceptionDetails))
        ]);

    private string? RedactOptional(string? value) => value is null ? null : _redactor.Redact(value);

    private void Publish(LogEntry entry)
    {
        var handlers = EntryLogged;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<LogEntry> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(entry);
            }
            catch (Exception)
            {
                // Несправний споживач UI не повинен зупиняти журнал та інших підписників.
            }
        }
    }

    private sealed class FileFailureListener(Action onFailure) : ILoggingFailureListener
    {
        public void OnLoggingFailed(object sender, LoggingFailureKind kind, string message,
            IReadOnlyCollection<LogEvent>? events, Exception? exception) => onFailure();
    }
}
