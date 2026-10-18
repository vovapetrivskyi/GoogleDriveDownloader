using System.Text.Json.Nodes;
using GoogleDriveDownloader.Application.Configuration;
using GoogleDriveDownloader.Application.Logging;
using GoogleDriveDownloader.Infrastructure.Configuration;

namespace GoogleDriveDownloader.Tests;

public sealed class ConfigurationLoaderTests
{
    [Fact]
    public async Task ShippedConfigurationIsExternalAndContainsEmptyCredentials()
    {
        var settings = await new ConfigurationLoader().LoadAsync();

        Assert.Empty(settings.GoogleOAuth.ClientId);
        Assert.Empty(settings.GoogleOAuth.ClientSecret);
        Assert.False(settings.GoogleOAuth.IsConfigured);
        Assert.Equal(GoogleOAuthSettings.DriveReadonlyScope, settings.GoogleOAuth.Scope);
        Assert.Equal(1, settings.Download.MaxConcurrentDownloads);
        Assert.Equal(3, settings.Download.RetryCount);
        Assert.Equal(30, settings.Logging.RetentionDays);
    }

    [Fact]
    public async Task LoadsFromSpecifiedDirectoryAndUsesDefaultsForMissingSettings()
    {
        using var directory = new TestDirectory();
        await WriteConfiguration(directory, new JsonObject());

        var settings = await new ConfigurationLoader().LoadAsync(directory.Path);

        Assert.False(settings.GoogleOAuth.IsConfigured);
        Assert.Equal(100, settings.Download.RequestTimeoutSeconds);
        Assert.Equal(300, settings.Download.AuthorizationTimeoutSeconds);
        Assert.Equal("Rename", settings.Download.DuplicatePolicy);
        Assert.Equal(ExportSettings.Docx, settings.Export.DocsMimeType);
        Assert.Equal(ExportSettings.Xlsx, settings.Export.SheetsMimeType);
        Assert.Equal(ExportSettings.Pptx, settings.Export.SlidesMimeType);
    }

    [Fact]
    public async Task AppliesValidCustomSettings()
    {
        using var directory = new TestDirectory();
        var json = JsonNode.Parse("""
            {
              "GoogleOAuth": { "ClientId": "synthetic-client", "ClientSecret": "synthetic-secret" },
              "Download": { "RetryCount": 0, "RequestTimeoutSeconds": 120, "AuthorizationTimeoutSeconds": 600 },
              "Logging": { "MinimumLevel": "warning", "RetentionDays": 7 }
            }
            """)!.AsObject();
        await WriteConfiguration(directory, json);

        var settings = await new ConfigurationLoader().LoadAsync(directory.Path);

        Assert.True(settings.GoogleOAuth.IsConfigured);
        Assert.Equal("synthetic-client", settings.GoogleOAuth.ClientId);
        Assert.Equal(0, settings.Download.RetryCount);
        Assert.Equal(120, settings.Download.RequestTimeoutSeconds);
        Assert.Equal(600, settings.Download.AuthorizationTimeoutSeconds);
        Assert.Equal(LogSeverity.Warning, settings.Logging.MinimumLevel);
        Assert.Equal(7, settings.Logging.RetentionDays);
    }

    [Theory]
    [InlineData("Download", "MaxConcurrentDownloads", "2")]
    [InlineData("Download", "RetryCount", "4")]
    [InlineData("Download", "RetryCount", "-1")]
    [InlineData("Download", "RetryCount", "three")]
    [InlineData("Download", "RequestTimeoutSeconds", "0")]
    [InlineData("Download", "AuthorizationTimeoutSeconds", "3601")]
    [InlineData("Download", "DuplicatePolicy", "Overwrite")]
    [InlineData("GoogleOAuth", "Scope", "https://www.googleapis.com/auth/drive")]
    [InlineData("Export", "DocsMimeType", "application/pdf")]
    [InlineData("Export", "SheetsMimeType", "text/csv")]
    [InlineData("Export", "SlidesMimeType", "application/pdf")]
    [InlineData("Logging", "MinimumLevel", "Debug")]
    [InlineData("Logging", "MinimumLevel", "2")]
    [InlineData("Logging", "RetentionDays", "0")]
    [InlineData("Logging", "RetentionDays", "3651")]
    public async Task RejectsUnsupportedOrInvalidSetting(string section, string key, string value)
    {
        using var directory = new TestDirectory();
        await WriteConfiguration(directory, new JsonObject { [section] = new JsonObject { [key] = value } });

        var exception = await Assert.ThrowsAsync<ConfigurationException>(() => new ConfigurationLoader().LoadAsync(directory.Path));

        Assert.Contains($"{section}:{key}", exception.Message);
    }

    [Fact]
    public async Task RejectsNonScalarSetting()
    {
        using var directory = new TestDirectory();
        await WriteConfiguration(directory, new JsonObject
        {
            ["Logging"] = new JsonObject { ["RetentionDays"] = new JsonArray(30, 60) }
        });

        await Assert.ThrowsAsync<ConfigurationException>(() => new ConfigurationLoader().LoadAsync(directory.Path));
    }

    [Theory]
    [InlineData("{\"Logging\":\"synthetic-wrong-shape\"}")]
    [InlineData("{\"Download\":[{\"RetryCount\":1}]}")]
    [InlineData("{\"GoogleOAuth\":true}")]
    public async Task RejectsSectionWithWrongShape(string json)
    {
        using var directory = new TestDirectory();
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory.Path, "appsettings.json"), json);

        var exception = await Assert.ThrowsAsync<ConfigurationException>(() => new ConfigurationLoader().LoadAsync(directory.Path));

        Assert.Contains("JSON-об'єкт", exception.Message);
        Assert.DoesNotContain("synthetic-wrong-shape", exception.ToString());
    }

    [Theory]
    [InlineData("ClientId")]
    [InlineData("ClientSecret")]
    public async Task IncompleteCredentialsProduceSafeError(string key)
    {
        using var directory = new TestDirectory();
        const string value = "synthetic-sensitive-value";
        await WriteConfiguration(directory, new JsonObject { ["GoogleOAuth"] = new JsonObject { [key] = value } });

        var exception = await Assert.ThrowsAsync<ConfigurationException>(() => new ConfigurationLoader().LoadAsync(directory.Path));

        Assert.Contains("обидва", exception.Message);
        Assert.DoesNotContain(value, exception.ToString());
    }

    [Theory]
    [InlineData("{\"ClientSecret\": \"synthetic-sensitive-value\", broken}")]
    [InlineData("[]")]
    [InlineData("{\"Logging\": {\"MinimumLevel\": \"Error\", \"MinimumLevel\": \"Warning\"}}")]
    public async Task MalformedJsonProducesSafeError(string json)
    {
        using var directory = new TestDirectory();
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory.Path, "appsettings.json"), json);

        var exception = await Assert.ThrowsAsync<ConfigurationException>(() => new ConfigurationLoader().LoadAsync(directory.Path));

        Assert.Contains("appsettings.json", exception.Message);
        Assert.DoesNotContain("synthetic-sensitive-value", exception.ToString());
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task MissingConfigurationProducesActionableError()
    {
        using var directory = new TestDirectory();

        var exception = await Assert.ThrowsAsync<ConfigurationException>(() => new ConfigurationLoader().LoadAsync(directory.Path));

        Assert.Contains("відсутній appsettings.json", exception.Message);
    }

    [Fact]
    public async Task ReadingHonorsCancellation()
    {
        using var directory = new TestDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ConfigurationLoader().LoadAsync(directory.Path, cancellation.Token));
    }

    [Fact]
    public async Task ReadingReleasesConfigurationFileWithoutChangingIt()
    {
        using var directory = new TestDirectory();
        await WriteConfiguration(directory, new JsonObject());
        var path = System.IO.Path.Combine(directory.Path, "appsettings.json");
        var original = await File.ReadAllBytesAsync(path);

        await new ConfigurationLoader().LoadAsync(directory.Path);

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static Task WriteConfiguration(TestDirectory directory, JsonObject configuration) =>
        File.WriteAllTextAsync(System.IO.Path.Combine(directory.Path, "appsettings.json"), configuration.ToJsonString());
}
