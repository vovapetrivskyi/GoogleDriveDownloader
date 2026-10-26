using ClosedXML.Excel;
using GoogleDriveDownloader.Application.Batch;
using GoogleDriveDownloader.Application.Configuration;
using GoogleDriveDownloader.Application.Drive;
using GoogleDriveDownloader.Application.Input;
using GoogleDriveDownloader.Application.Logging;
using GoogleDriveDownloader.Application.Storage;
using GoogleDriveDownloader.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GoogleDriveDownloader.Tests;

public sealed class BootstrapperTests
{
    [Fact]
    public async Task JsonConfiguredHeadersReachExcelReaderThroughContainer()
    {
        using var directory = new TestDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "appsettings.json"), """
            { "Excel": { "NameColumnHeader": "Filename", "LinkColumnHeader": "Source URL" } }
            """);
        var settings = await new ConfigurationLoader().LoadAsync(directory.Path);
        var path = Path.Combine(directory.Path, "custom.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.AddWorksheet("Input");
            sheet.Cell("B1").Value = "Filename";
            sheet.Cell("D1").Value = "Source URL";
            sheet.Cell("B2").Value = "Report";
            sheet.Cell("D2").Value = "https://drive.google.com/open?id=SYNTHETIC_PDF_001";
            workbook.SaveAs(path);
        }
        await using var services = Bootstrapper.BuildServices(settings, Path.Combine(directory.Path, "profile"));
        var result = await services.GetRequiredService<ExcelReader>().ReadAsync(path);
        var row = Assert.Single(result.Rows);
        Assert.True(row.IsValid);
        Assert.Equal("Report", row.DocumentName);
        Assert.Equal("SYNTHETIC_PDF_001", row.Link!.FileId);
    }

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
