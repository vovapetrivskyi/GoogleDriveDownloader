using GoogleDriveDownloader.Application.Configuration;
using GoogleDriveDownloader.Application.Logging;
using GoogleDriveDownloader.Infrastructure.Logging;
using GoogleDriveDownloader.Infrastructure.Authentication;
using GoogleDriveDownloader.Presentation;
using Microsoft.Extensions.DependencyInjection;

namespace GoogleDriveDownloader;

public static class Bootstrapper
{
    public static ServiceProvider BuildServices(AppSettings settings, string? userDataDirectory = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(settings);
        services.AddSingleton<ILoggingService>(_ => new LoggingService(settings, userDataDirectory));
        services.AddSingleton(provider => new OAuthConfigurationService(settings,
            provider.GetRequiredService<ILoggingService>(), userDataDirectory));
        services.AddTransient<MainForm>();
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }
}
