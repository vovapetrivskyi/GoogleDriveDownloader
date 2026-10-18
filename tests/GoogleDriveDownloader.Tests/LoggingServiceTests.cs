using System.Collections.Concurrent;
using System.Text.Json;
using GoogleDriveDownloader.Application.Configuration;
using GoogleDriveDownloader.Application.Logging;
using GoogleDriveDownloader.Infrastructure.Logging;

namespace GoogleDriveDownloader.Tests;

public sealed class LoggingServiceTests
{
    [Fact]
    public async Task FileAndSubscribersReceiveMatchingOrderedStructuredEvents()
    {
        using var directory = new TestDirectory();
        var entries = new ConcurrentQueue<LogEntry>();
        var logger = new LoggingService(new AppSettings(), directory.Path);
        logger.EntryLogged += entries.Enqueue;
        var context = new LogContext(12, "Документ", "synthetic-file-id", "Failed", 403);

        logger.Write(LogSeverity.Information, "Початок");
        logger.Write(LogSeverity.Warning, "Попередження");
        logger.Write(LogSeverity.Error, "Помилка", context, new IOException("synthetic-diagnostic"));
        await logger.DisposeAsync();

        var actual = entries.ToArray();
        Assert.Equal(3, actual.Length);
        Assert.Equal(context, actual[2].Context);
        Assert.Contains("IOException", actual[2].ExceptionDetails);
        var lines = await File.ReadAllLinesAsync(SingleLogFile(directory));
        Assert.Equal(actual.Length, lines.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            using var json = JsonDocument.Parse(lines[index]);
            Assert.Equal(actual[index].Timestamp, json.RootElement.GetProperty("Timestamp").GetDateTimeOffset());
            Assert.Equal(actual[index].Level.ToString(), json.RootElement.GetProperty("Level").GetString());
            var properties = json.RootElement.GetProperty("Properties");
            Assert.Equal(actual[index].Message, properties.GetProperty("Message").GetString());
            if (index == 2)
            {
                Assert.Equal(12, properties.GetProperty("RowNumber").GetInt32());
                Assert.Equal("Документ", properties.GetProperty("DocumentName").GetString());
                Assert.Equal("synthetic-file-id", properties.GetProperty("FileId").GetString());
                Assert.Equal("Failed", properties.GetProperty("Status").GetString());
                Assert.Equal(403, properties.GetProperty("HttpStatusCode").GetInt32());
                Assert.Equal(actual[index].ExceptionDetails, properties.GetProperty("ExceptionDetails").GetString());
            }
        }
    }

    [Fact]
    public async Task SecretsAreRedactedBeforeEitherOutputIncludingExceptionAndContext()
    {
        using var directory = new TestDirectory();
        var settings = new AppSettings
        {
            GoogleOAuth = new GoogleOAuthSettings { ClientId = "synthetic-client-id", ClientSecret = "synthetic secret/&" }
        };
        var entries = new ConcurrentQueue<LogEntry>();
        var logger = new LoggingService(settings, directory.Path);
        logger.EntryLogged += entries.Enqueue;
        var details = """
            Authorization: Bearer synthetic-bearer
            {"access_token":"synthetic-access", "refresh_token":"synthetic-refresh", "password":"synthetic password"}
            https://example.invalid/?client_secret=synthetic-secret&resourcekey=synthetic-resource
            ya29.synthetic-unlabelled 1//synthetic-offline
            """;

        logger.Write(LogSeverity.Error,
            $"synthetic-client-id synthetic secret/& {Uri.EscapeDataString(settings.GoogleOAuth.ClientSecret)} {details}",
            new LogContext(DocumentName: "synthetic-client-id", FileId: "synthetic secret/&", Status: details),
            new IOException(details, new Exception("client_secret='synthetic inner secret'")));
        await logger.DisposeAsync();

        var entry = Assert.Single(entries);
        var text = entry.ToString() + await File.ReadAllTextAsync(SingleLogFile(directory));
        foreach (var secret in new[]
        {
            "synthetic-client-id", "synthetic secret/&", Uri.EscapeDataString(settings.GoogleOAuth.ClientSecret),
            "synthetic-bearer", "synthetic-access", "synthetic-refresh", "synthetic password",
            "synthetic-secret", "synthetic-resource", "synthetic-unlabelled", "synthetic-offline", "synthetic inner secret"
        })
        {
            Assert.DoesNotContain(secret, text);
        }

        Assert.Contains("[REDACTED]", text);
        Assert.Contains("IOException", entry.ExceptionDetails);
    }

    [Fact]
    public async Task FileFailurePreservesSubscriberMessagesAndReportsOneSafeWarning()
    {
        using var directory = new TestDirectory();
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory.Path, "logs"), "not a directory");
        var entries = new ConcurrentQueue<LogEntry>();
        var logger = new LoggingService(new AppSettings(), directory.Path);
        logger.EntryLogged += entries.Enqueue;

        logger.Write(LogSeverity.Information, "Перша подія");
        logger.Write(LogSeverity.Error, "Друга подія");
        await logger.DisposeAsync();

        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, entry => entry.Message == "Перша подія");
        Assert.Contains(entries, entry => entry.Message == "Друга подія");
        var warning = Assert.Single(entries, entry => entry.Context.Status == "FileLoggingUnavailable");
        Assert.Equal(LogSeverity.Warning, warning.Level);
        Assert.DoesNotContain(directory.Path, warning.Message);
    }

    [Fact]
    public async Task SubscriberFailureDoesNotStopFileOrOtherSubscribers()
    {
        using var directory = new TestDirectory();
        var entries = new ConcurrentQueue<LogEntry>();
        var logger = new LoggingService(new AppSettings(), directory.Path);
        logger.EntryLogged += _ => throw new InvalidOperationException("synthetic subscriber failure");
        logger.EntryLogged += entries.Enqueue;

        logger.Write(LogSeverity.Information, "Подія");
        await logger.DisposeAsync();

        Assert.Single(entries);
        Assert.Single(await File.ReadAllLinesAsync(SingleLogFile(directory)));
    }

    [Fact]
    public async Task MinimumLevelFiltersBothOutputs()
    {
        using var directory = new TestDirectory();
        var settings = new AppSettings { Logging = new LoggingSettings { MinimumLevel = LogSeverity.Error } };
        var entries = new ConcurrentQueue<LogEntry>();
        var logger = new LoggingService(settings, directory.Path);
        logger.EntryLogged += entries.Enqueue;

        logger.Write(LogSeverity.Information, "Інформація");
        logger.Write(LogSeverity.Warning, "Попередження");
        logger.Write(LogSeverity.Error, "Помилка");
        await logger.DisposeAsync();

        Assert.Equal(LogSeverity.Error, Assert.Single(entries).Level);
        Assert.Single(await File.ReadAllLinesAsync(SingleLogFile(directory)));
    }

    [Fact]
    public async Task DailyRollingRemovesLogsOlderThanConfiguredAgeOnly()
    {
        using var directory = new TestDirectory();
        var logs = Directory.CreateDirectory(System.IO.Path.Combine(directory.Path, "logs"));
        var old = System.IO.Path.Combine(logs.FullName, $"log-{DateTime.Now.AddDays(-5):yyyyMMdd}.jsonl");
        var recent = System.IO.Path.Combine(logs.FullName, $"log-{DateTime.Now.AddDays(-1):yyyyMMdd}.jsonl");
        var unrelated = System.IO.Path.Combine(logs.FullName, "unrelated.txt");
        foreach (var path in new[] { old, recent, unrelated })
        {
            await File.WriteAllTextAsync(path, "existing");
        }

        var settings = new AppSettings { Logging = new LoggingSettings { RetentionDays = 2 } };
        var logger = new LoggingService(settings, directory.Path);
        logger.Write(LogSeverity.Information, "Сьогодні");
        await logger.DisposeAsync();

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(System.IO.Path.Combine(logs.FullName, $"log-{DateTime.Now:yyyyMMdd}.jsonl")));
    }

    [Fact]
    public async Task DisposalDrainsQueueIsIdempotentAndReleasesFile()
    {
        using var directory = new TestDirectory();
        var logger = new LoggingService(new AppSettings(), directory.Path);
        for (var index = 0; index < 100; index++)
        {
            logger.Write(LogSeverity.Information, $"Подія {index}");
        }

        await logger.DisposeAsync();
        await logger.DisposeAsync();
        logger.Write(LogSeverity.Information, "Після завершення");

        var path = SingleLogFile(directory);
        Assert.Equal(100, (await File.ReadAllLinesAsync(path)).Length);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static string SingleLogFile(TestDirectory directory) =>
        Assert.Single(Directory.GetFiles(System.IO.Path.Combine(directory.Path, "logs"), "log-*.jsonl"));
}
