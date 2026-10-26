using System.Globalization;
using GoogleDriveDownloader.Application.Configuration;
using GoogleDriveDownloader.Application.Logging;
using Microsoft.Extensions.Configuration;

namespace GoogleDriveDownloader.Infrastructure.Configuration;

public sealed class ConfigurationLoader
{
    public Task<AppSettings> LoadAsync(string? baseDirectory = null, CancellationToken cancellationToken = default)
    {
        // JSON-провайдер синхронний, тому читання й перевірка не виконуються в UI-потоці.
        return Task.Run(() => Load(baseDirectory ?? AppContext.BaseDirectory, cancellationToken), cancellationToken);
    }

    private static AppSettings Load(string baseDirectory, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var configuration = new ConfigurationBuilder()
                .SetBasePath(baseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
                .Build();
            using var configurationLifetime = configuration as IDisposable;

            foreach (var name in new[] { "Excel", "GoogleOAuth", "Download", "Export", "Logging" })
            {
                var section = configuration.GetSection(name);
                if (section.Value is not null || section.GetChildren().Any(child => int.TryParse(child.Key, out _)))
                {
                    throw InvalidSetting(name, "JSON-об'єкт");
                }
            }

            var level = ReadText(configuration, "Logging:MinimumLevel", "Information");
            var severity = level.ToUpperInvariant() switch
            {
                "INFORMATION" => LogSeverity.Information,
                "WARNING" => LogSeverity.Warning,
                "ERROR" => LogSeverity.Error,
                _ => throw InvalidSetting("Logging:MinimumLevel", "Information, Warning або Error")
            };

            var excelDefaults = new ExcelSettings();
            var nameHeader = ReadText(configuration, "Excel:NameColumnHeader", excelDefaults.NameColumnHeader);
            var linkHeader = ReadText(configuration, "Excel:LinkColumnHeader", excelDefaults.LinkColumnHeader);
            if (string.IsNullOrWhiteSpace(nameHeader))
                throw InvalidSetting("Excel:NameColumnHeader", "непорожня назва заголовка");
            if (string.IsNullOrWhiteSpace(linkHeader))
                throw InvalidSetting("Excel:LinkColumnHeader", "непорожня назва заголовка");
            if (nameHeader.Equals(linkHeader, StringComparison.OrdinalIgnoreCase))
                throw InvalidSetting("Excel:LinkColumnHeader", "назва, відмінна від Excel:NameColumnHeader без урахування регістру");

            var settings = new AppSettings
            {
                Excel = new ExcelSettings { NameColumnHeader = nameHeader, LinkColumnHeader = linkHeader },
                GoogleOAuth = new GoogleOAuthSettings
                {
                    ClientId = ReadText(configuration, "GoogleOAuth:ClientId", ""),
                    ClientSecret = ReadText(configuration, "GoogleOAuth:ClientSecret", ""),
                    Scope = ReadSupported(configuration, "GoogleOAuth:Scope", GoogleOAuthSettings.DriveReadonlyScope)
                },
                Download = new DownloadSettings
                {
                    MaxConcurrentDownloads = ReadInteger(configuration, "Download:MaxConcurrentDownloads", 1, 1, 1),
                    RetryCount = ReadInteger(configuration, "Download:RetryCount", 3, 0, 3),
                    RequestTimeoutSeconds = ReadInteger(configuration, "Download:RequestTimeoutSeconds", 100, 1, 3600),
                    AuthorizationTimeoutSeconds = ReadInteger(configuration, "Download:AuthorizationTimeoutSeconds", 300, 1, 3600),
                    DuplicatePolicy = ReadSupported(configuration, "Download:DuplicatePolicy", "Rename")
                },
                Export = new ExportSettings
                {
                    DocsMimeType = ReadSupported(configuration, "Export:DocsMimeType", ExportSettings.Docx),
                    SheetsMimeType = ReadSupported(configuration, "Export:SheetsMimeType", ExportSettings.Xlsx),
                    SlidesMimeType = ReadSupported(configuration, "Export:SlidesMimeType", ExportSettings.Pptx)
                },
                Logging = new LoggingSettings
                {
                    MinimumLevel = severity,
                    RetentionDays = ReadInteger(configuration, "Logging:RetentionDays", 30, 1, 3650)
                }
            };

            cancellationToken.ThrowIfCancellationRequested();
            return settings;
        }
        catch (FileNotFoundException)
        {
            throw new ConfigurationException("Поруч із EXE відсутній appsettings.json. Відновіть зовнішній файл конфігурації з дистрибутива.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            // Повідомлення парсера може містити фрагмент JSON із секретом; його не передаємо далі.
            throw new ConfigurationException("Не вдалося прочитати appsettings.json. Перевірте синтаксис JSON і права доступу до файла поруч із EXE.");
        }
    }

    private static string ReadText(IConfiguration configuration, string key, string fallback)
    {
        var section = configuration.GetSection(key);
        if (section.GetChildren().Any())
        {
            throw InvalidSetting(key, "скалярне значення");
        }

        return section.Value?.Trim() ?? fallback;
    }

    private static string ReadSupported(IConfiguration configuration, string key, string supported)
    {
        var value = ReadText(configuration, key, supported);
        if (!string.Equals(value, supported, StringComparison.Ordinal))
        {
            throw InvalidSetting(key, supported);
        }

        return value;
    }

    private static int ReadInteger(IConfiguration configuration, string key, int fallback, int minimum, int maximum)
    {
        var value = ReadText(configuration, key, fallback.ToString(CultureInfo.InvariantCulture));
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < minimum || number > maximum)
        {
            throw InvalidSetting(key, $"ціле число від {minimum} до {maximum}");
        }

        return number;
    }

    private static ConfigurationException InvalidSetting(string key, string expected) =>
        new($"Неприпустиме налаштування {key} у appsettings.json. Очікується: {expected}.");
}

public sealed class ConfigurationException(string message) : Exception(message);
