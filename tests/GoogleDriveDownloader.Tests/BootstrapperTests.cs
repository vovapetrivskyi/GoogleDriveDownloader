using GoogleDriveDownloader.Application.Batch;
using GoogleDriveDownloader.Application.Configuration;
using GoogleDriveDownloader.Application.Drive;
using GoogleDriveDownloader.Application.Input;
using GoogleDriveDownloader.Application.Logging;
using GoogleDriveDownloader.Application.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace GoogleDriveDownloader.Tests;

public sealed class BootstrapperTests
{
    [Fact]
    public async Task ContainerOwnsSingletonServicesAndFlushesLoggingOnDisposal()
    {
        using var directory = new TestDirectory();
        var settings = new AppSettings();
        var services = Bootstrapper.BuildServices(settings, directory.Path);

        Assert.Same(settings, services.GetRequiredService<AppSettings>());
        Assert.NotNull(services.GetRequiredService<ExcelReader>());
        Assert.NotNull(services.GetRequiredService<DriveLinkParser>());
        Assert.NotNull(services.GetRequiredService<FileNameResolver>());
        Assert.Same(services.GetRequiredService<BatchDownloadService>(), services.GetRequiredService<BatchDownloadService>());
        Assert.Equal(settings.Download.RetryCount, services.GetRequiredService<DownloadRetryPolicy>().MaximumRetries);
        Assert.Same(services.GetRequiredService<IFileStorageService>(), services.GetRequiredService<IFileStorageService>());
        Assert.Same(services.GetRequiredService<IGoogleDriveService>(), services.GetRequiredService<IGoogleDriveService>());
        var logging = services.GetRequiredService<ILoggingService>();
        Assert.Same(logging, services.GetRequiredService<ILoggingService>());
        logging.Write(LogSeverity.Information, "DI shutdown test");

        await services.DisposeAsync();

        var file = Assert.Single(Directory.GetFiles(System.IO.Path.Combine(directory.Path, "logs")));
        Assert.Contains("DI shutdown test", await File.ReadAllTextAsync(file));
        using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
}
