using GoogleDriveDownloader.Application.Logging;
using GoogleDriveDownloader.Infrastructure.Configuration;
using GoogleDriveDownloader.Presentation;
using Microsoft.Extensions.DependencyInjection;

namespace GoogleDriveDownloader;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        ServiceProvider? services = null;
        ILoggingService? logging = null;
        ThreadExceptionEventHandler threadExceptionHandler = (_, args) =>
        {
            logging?.Write(LogSeverity.Error, "Необроблена помилка інтерфейсу.", exception: args.Exception);
            MessageBox.Show("Не вдалося виконати дію. Перевірте журнал програми.", "Google Drive Downloader",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        try
        {
            // Синхронні межі STA потрібні лише до запуску message loop та після його завершення.
            var settings = new ConfigurationLoader().LoadAsync().GetAwaiter().GetResult();
            services = Bootstrapper.BuildServices(settings);
            logging = services.GetRequiredService<ILoggingService>();
            System.Windows.Forms.Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            System.Windows.Forms.Application.ThreadException += threadExceptionHandler;
            System.Windows.Forms.Application.Run(services.GetRequiredService<MainForm>());
        }
        catch (ConfigurationException exception)
        {
            MessageBox.Show(exception.Message, "Помилка конфігурації", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception exception)
        {
            logging?.Write(LogSeverity.Error, "Не вдалося запустити застосунок.", exception: exception);
            MessageBox.Show("Не вдалося запустити застосунок. Перевірте конфігурацію та журнал програми.",
                "Google Drive Downloader", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            System.Windows.Forms.Application.ThreadException -= threadExceptionHandler;
            if (services is not null)
            {
                logging?.Write(LogSeverity.Information, "Застосунок завершено.", new LogContext(Status: "Stopped"));
                Task.Run(async () => await services.DisposeAsync().ConfigureAwait(false)).GetAwaiter().GetResult();
            }
        }
    }
}
