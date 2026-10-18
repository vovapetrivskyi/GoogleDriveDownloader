using System.Windows.Forms;

namespace GoogleDriveDownloader.Tests;

[CollectionDefinition("WinForms", DisableParallelization = true)]
public sealed class WinFormsCollection;

internal static class WinFormsTestHost
{
    public static Task RunAsync(Func<Task> test)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            try
            {
                System.Windows.Forms.Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, threadScope: true);
                Control.CheckForIllegalCrossThreadCalls = true;
                using var host = new Form { Opacity = 0, ShowInTaskbar = false };
                host.Shown += async (_, _) =>
                {
                    try { await test(); }
                    catch (Exception exception) { failure = exception; }
                    finally { host.Close(); }
                };
                System.Windows.Forms.Application.Run(host);
                if (failure is null) completed.TrySetResult();
                else completed.TrySetException(failure);
            }
            catch (Exception exception) { completed.TrySetException(exception); }
        }) { IsBackground = true, Name = "WinForms test STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completed.Task.WaitAsync(TimeSpan.FromSeconds(40));
    }

    public static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
