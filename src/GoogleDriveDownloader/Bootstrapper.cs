using GoogleDriveDownloader.Application.Batch;
using GoogleDriveDownloader.Application.Configuration;
using GoogleDriveDownloader.Application.Drive;
using GoogleDriveDownloader.Application.Input;
using GoogleDriveDownloader.Application.Logging;
using GoogleDriveDownloader.Application.Storage;
using GoogleDriveDownloader.Infrastructure.Logging;
using GoogleDriveDownloader.Infrastructure.Authentication;
using GoogleDriveDownloader.Infrastructure.Drive;
using GoogleDriveDownloader.Infrastructure.Storage;
using GoogleDriveDownloader.Presentation;
using Microsoft.Extensions.DependencyInjection;

namespace GoogleDriveDownloader;

public static class Bootstrapper
{
    public static ServiceProvider BuildServices(AppSettings settings, string? userDataDirectory = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(settings);
        services.AddSingleton<DriveLinkParser>();
        services.AddSingleton<ExcelReader>();
        services.AddSingleton<FileNameResolver>();
        services.AddSingleton<DownloadRetryPolicy>();
        services.AddSingleton<BatchDownloadService>();
        services.AddSingleton<IFileStorageService, FileStorageService>();
        services.AddSingleton<ILoggingService>(_ => new LoggingService(settings, userDataDirectory));
        services.AddSingleton(provider => new OAuthConfigurationService(settings,
            provider.GetRequiredService<ILoggingService>(), userDataDirectory));
        services.AddSingleton(provider => new GoogleAuthService(settings,
            provider.GetRequiredService<OAuthConfigurationService>(), provider.GetRequiredService<ILoggingService>()));
        services.AddSingleton<IGoogleDriveService>(provider => new GoogleDriveService(settings,
            provider.GetRequiredService<GoogleAuthService>()));
        services.AddTransient<MainForm>();
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }
}
