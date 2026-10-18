using GoogleDriveDownloader.Application.Configuration;
using GoogleDriveDownloader.Application.Logging;
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
        var logging = services.GetRequiredService<ILoggingService>();
        Assert.Same(logging, services.GetRequiredService<ILoggingService>());
        logging.Write(LogSeverity.Information, "DI shutdown test");

        await services.DisposeAsync();

        var file = Assert.Single(Directory.GetFiles(System.IO.Path.Combine(directory.Path, "logs")));
        Assert.Contains("DI shutdown test", await File.ReadAllTextAsync(file));
        using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
}
