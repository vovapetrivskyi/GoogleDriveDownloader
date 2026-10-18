using System.Drawing;
using System.Windows.Forms;
using GoogleDriveDownloader.Application.Drive;
using GoogleDriveDownloader.Application.Logging;

namespace GoogleDriveDownloader.Tests;

[Collection("WinForms")]
public sealed class MainFormTests
{
    [Fact]
    public Task CompleteBatchShowsStatisticsColoredRowLogsAndAllowsAnotherRun() => WinFormsTestHost.RunAsync(async () =>
    {
        await using var context = new MainFormTestContext();
        await context.OpenAsync(rows: 4);
        context.WriteWorkbook(4, invalidLast: true);
        context.Drive.Metadata = (link, _, _) => link.FileId == "File_3"
            ? throw new DriveServiceException(DriveErrorCode.AccessDenied, "Доступ заборонено.", 403)
            : Task.FromResult(GoogleDriveServiceTests.Metadata() with { FileId = link.FileId });
        var form = context.Form;
        var uiThread = Environment.CurrentManagedThreadId;
        var updates = new List<int>();
        form.Get<Label>("Statistics").TextChanged += (_, _) => updates.Add(Environment.CurrentManagedThreadId);
        var log = form.Get<RichTextBox>("Log");
        log.TextChanged += (_, _) => updates.Add(Environment.CurrentManagedThreadId);

        form.Press("StartDownload");
        await context.FinishedAsync();
        await WinFormsTestHost.UntilAsync(() => log.Text.Contains("Готово.", StringComparison.Ordinal));

        Assert.Equal("Готово. Успішно: 2, помилок: 2, пропущено: 0.", form.Get<Label>("BatchStatus").Text);
        Assert.Equal("Оброблено: 4 / 4. Успішно: 2, помилок: 2, пропущено: 0.", form.Get<Label>("Statistics").Text);
        Assert.Equal(4, form.Get<ProgressBar>("DownloadProgress").Maximum);
        Assert.Equal(4, form.Get<ProgressBar>("DownloadProgress").Value);
        Assert.Equal(2, Directory.GetFiles(context.OutputDirectory).Length);
        Assert.Contains("Рядок 3: Документ 3", log.Text);
        Assert.Contains("HTTP 403", log.Text);
        Assert.Matches(@"\[\d{2}:\d{2}:\d{2}\]", log.Text);
        AssertLogColor(log, "Завантажено:", Color.ForestGreen);
        AssertLogColor(log, "Помилка:", Color.Firebrick);
        AssertLogColor(log, "Початок обробки.", SystemColors.WindowText);
        Assert.NotEmpty(updates);
        Assert.All(updates, thread => Assert.Equal(uiThread, thread));
        Assert.True(log.ReadOnly);
        Assert.Equal(RichTextBoxScrollBars.Vertical, log.ScrollBars);
        Assert.True(log.Height >= 100);
        Assert.False(form.Get<Button>("CancelOperation").Enabled);

        form.Press("StartDownload");
        await context.FinishedAsync();
        await Task.Yield();
        Assert.Equal(4, Directory.GetFiles(context.OutputDirectory).Length);
        Assert.Contains(" (2).pdf", string.Join(",", Directory.GetFiles(context.OutputDirectory)));
        Assert.Equal("Готово. Успішно: 2, помилок: 2, пропущено: 0.", form.Get<Label>("BatchStatus").Text);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CancelDuringWriteOrRetryKeepsUiResponsiveAndParametersLocked(bool duringRetry) => WinFormsTestHost.RunAsync(async () =>
    {
        await using var context = new MainFormTestContext();
        await context.OpenAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Stream? destination = null;
        context.Drive.Download = async (stream, _, token) =>
        {
            destination = stream;
            await stream.WriteAsync(new byte[] { 9, 9 }, token);
            if (duringRetry) throw new DriveServiceException(DriveErrorCode.NetworkFailure, "Мережа недоступна.", isTransient: true);
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 2;
        };
        context.Retry.OnDelay = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        var form = context.Form;
        form.Press("StartDownload");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        foreach (var control in new[] { "StartDownload", "ImportOAuth", "SignIn", "SignOut", "ChangeAccount",
                     "WorkbookPath", "DirectoryPath", "BrowseWorkbook", "BrowseDirectory" })
            Assert.False(Assert.Single(form.Controls.Find(control, true)).Enabled);
        Assert.True(form.Get<Button>("CancelOperation").Enabled);
        form.Press("StartDownload");
        form.Press("SignOut");
        Assert.True(context.Auth.IsAuthorized);
        Assert.Equal(1, context.Drive.MetadataCalls);
        var pulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        form.BeginInvoke(new Action(() => pulse.TrySetResult()));
        await pulse.Task.WaitAsync(TimeSpan.FromSeconds(3));
        form.Press("CancelOperation");
        await context.FinishedAsync();

        Assert.StartsWith("Скасовано.", form.Get<Label>("BatchStatus").Text);
        Assert.Contains("пропущено: 3", form.Get<Label>("Statistics").Text);
        Assert.Equal(0, form.Get<ProgressBar>("DownloadProgress").Value);
        Assert.Empty(Directory.GetFiles(context.OutputDirectory));
        Assert.NotNull(destination);
        Assert.False(destination.CanWrite);
        Assert.False(form.Get<Button>("CancelOperation").Enabled);
        context.Drive.Download = async (stream, _, token) => { await stream.WriteAsync(new byte[] { 1 }, token); return 1; };
        form.Press("StartDownload");
        await context.FinishedAsync();
        Assert.StartsWith("Готово.", form.Get<Label>("BatchStatus").Text);
        Assert.Equal(3, Directory.GetFiles(context.OutputDirectory).Length);
    });

    [Fact]
    public Task ClosingWaitsForCancelledWriterAndIgnoresLateLogNotifications() => WinFormsTestHost.RunAsync(async () =>
    {
        await using var context = new MainFormTestContext();
        await context.OpenAsync();
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Drive.Download = async (stream, _, token) =>
        {
            await stream.WriteAsync(new byte[] { 1, 2 }, token);
            writing.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cleaning.TrySetResult(); await release.Task; }
            return 2;
        };
        context.Form.Press("StartDownload");
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(8));
        try
        {
            context.Form.Close();
            await cleaning.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.False(context.Form.IsDisposed);
            Assert.False(context.Form.Get<Button>("CancelOperation").Enabled);
            Assert.False(context.Form.Get<Button>("StartDownload").Enabled);
            context.Form.Close();
            Assert.False(context.Form.IsDisposed);
            Assert.Single(Directory.GetFiles(context.OutputDirectory, "*.part"));
            await Task.Yield();
        }
        finally { release.TrySetResult(); }
        await WinFormsTestHost.UntilAsync(() => context.Form.IsDisposed);
        Assert.Empty(Directory.GetFiles(context.OutputDirectory));
        context.Scope.Logging.Write(LogSeverity.Information, "Пізнє повідомлення.");
        await Task.Yield();
    });

    [Fact]
    public Task ImportSignInChangeAccountAndSignOutAreConnectedAndGateDownload() => WinFormsTestHost.RunAsync(async () =>
    {
        await using var context = new MainFormTestContext(configured: false);
        await context.OpenAsync(authorize: false);
        var form = context.Form;
        Assert.False(form.Get<Button>("SignIn").Enabled);
        Assert.False(form.Get<Button>("StartDownload").Enabled);
        Assert.Contains("Імпортуйте", form.Get<Label>("OAuthStatus").Text);
        Assert.Contains("імпортуйте", form.Get<Label>("BatchStatus").Text);
        form.OAuthChoice = await context.Scope.SourceAsync();
        form.Press("ImportOAuth");
        await context.ReadyAsync();
        Assert.True(form.Get<Button>("SignIn").Enabled);
        Assert.False(form.Get<Button>("StartDownload").Enabled);
        form.Press("SignIn");
        await context.FinishedAsync();
        Assert.Equal("Авторизовано", form.Get<Label>("OAuthStatus").Text);
        Assert.Equal(1, context.Receiver.Calls);
        form.Press("ChangeAccount");
        await context.FinishedAsync();
        Assert.Equal(2, context.Receiver.Calls);
        form.Press("SignOut");
        await context.ReadyAsync();
        Assert.False(context.Auth.IsAuthorized);
        Assert.False(form.Get<Button>("StartDownload").Enabled);
        Assert.True(form.Get<Button>("SignIn").Enabled);
        Assert.Equal(0, context.Drive.MetadataCalls);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AuthorizationCanBeCancelledOrClosedWithoutLeavingReceiverActive(bool close) => WinFormsTestHost.RunAsync(async () =>
    {
        await using var context = new MainFormTestContext();
        await context.OpenAsync(authorize: false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Receiver.ReceiveAsync = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        };
        context.Form.Press("SignIn");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(context.Form.Get<Button>("CancelOperation").Enabled);
        Assert.False(context.Form.Get<TextBox>("WorkbookPath").Enabled);
        if (close)
        {
            context.Form.Close();
            await WinFormsTestHost.UntilAsync(() => context.Form.IsDisposed);
        }
        else
        {
            context.Form.Press("CancelOperation");
            await context.ReadyAsync();
            Assert.Equal("Операцію авторизації скасовано.", context.Form.Get<Label>("BatchStatus").Text);
            Assert.True(context.Form.Get<Button>("SignIn").Enabled);
        }
        Assert.False(context.Auth.IsAuthorized);
        Assert.Empty(context.Form.Messages);
    });

    [Theory]
    [InlineData("extension")]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("corrupt")]
    public Task InvalidInputIsReportedAndCanBeCorrectedWithoutRestart(string failure) => WinFormsTestHost.RunAsync(async () =>
    {
        await using var context = new MainFormTestContext();
        await context.OpenAsync(rows: 1);
        var form = context.Form;
        if (failure == "extension") form.Get<TextBox>("WorkbookPath").Text = "input.xls";
        if (failure == "file") form.Get<TextBox>("WorkbookPath").Text = context.WorkbookPath + ".xlsx";
        if (failure == "directory") form.Get<TextBox>("DirectoryPath").Text = Path.Combine(context.OutputDirectory, "missing");
        if (failure == "corrupt") await File.WriteAllTextAsync(context.WorkbookPath, "corrupt");
        form.Press("StartDownload");
        await context.FinishedAsync();
        Assert.Equal(0, context.Drive.MetadataCalls);
        Assert.DoesNotContain("Готово", form.Get<Label>("BatchStatus").Text);
        Assert.Equal(ProgressBarStyle.Blocks, form.Get<ProgressBar>("DownloadProgress").Style);
        Assert.Contains("Оброблено: 0 / 0", form.Get<Label>("Statistics").Text);
        context.WriteWorkbook(1);
        form.Get<TextBox>("WorkbookPath").Text = context.WorkbookPath;
        form.Get<TextBox>("DirectoryPath").Text = context.OutputDirectory;
        form.Press("StartDownload");
        await context.FinishedAsync();
        Assert.StartsWith("Готово.", form.Get<Label>("BatchStatus").Text);
        Assert.Single(Directory.GetFiles(context.OutputDirectory));
    });

    [Fact]
    public Task AuthenticationLossDuringBatchRequiresSignInAndShowsCriticalStatus() => WinFormsTestHost.RunAsync(async () =>
    {
        await using var context = new MainFormTestContext();
        await context.OpenAsync();
        context.Drive.Metadata = async (_, _, token) =>
        {
            await context.Auth.SignOutAsync(token);
            throw new DriveServiceException(DriveErrorCode.AuthenticationRequired, "Увійдіть повторно.");
        };
        context.Form.Press("StartDownload");
        await context.ReadyAsync();
        Assert.False(context.Form.Get<Button>("StartDownload").Enabled);
        Assert.True(context.Form.Get<Button>("SignIn").Enabled);
        Assert.StartsWith("Пакет зупинено через критичну помилку.", context.Form.Get<Label>("BatchStatus").Text);
        Assert.Contains("помилок: 1, пропущено: 2", context.Form.Get<Label>("Statistics").Text);
        Assert.Equal(1, context.Drive.MetadataCalls);
    });

    [Fact]
    public Task DialogCancellationAndFailurePreserveSelectedPathsAndReportSafeMessage() => WinFormsTestHost.RunAsync(async () =>
    {
        await using var context = new MainFormTestContext();
        await context.OpenAsync();
        context.Form.WorkbookChoice = null;
        context.Form.DirectoryChoice = null;
        context.Form.Press("BrowseWorkbook");
        context.Form.Press("BrowseDirectory");
        Assert.Equal(context.WorkbookPath, context.Form.Get<TextBox>("WorkbookPath").Text);
        Assert.Equal(context.OutputDirectory, context.Form.Get<TextBox>("DirectoryPath").Text);
        context.Form.FailDialog = true;
        context.Form.Press("BrowseWorkbook");
        Assert.DoesNotContain("DO-NOT-EXPOSE", Assert.Single(context.Form.Messages).Message);
        Assert.True(context.Form.Get<Button>("StartDownload").Enabled);
    });

    [Fact]
    public Task BackgroundLogsAreRedactedAndFileLoggingFailureDoesNotBlockDownload() => WinFormsTestHost.RunAsync(async () =>
    {
        await using var context = new MainFormTestContext();
        await context.OpenAsync(rows: 1);
        context.Scope.Logging.RegisterSensitiveValues("synthetic-ui-secret");
        await Task.Run(() =>
        {
            context.Scope.Logging.Write(LogSeverity.Information, "Маркер synthetic-ui-secret\r\nДругий рядок.",
                new LogContext(7, "Назва\nдокумента", Status: "Diagnostic"));
            context.Scope.Logging.Write(LogSeverity.Warning, "Файловий журнал недоступний.", new LogContext(Status: "FileLoggingUnavailable"));
        });
        var log = context.Form.Get<RichTextBox>("Log");
        await WinFormsTestHost.UntilAsync(() => log.Text.Contains("Другий рядок.", StringComparison.Ordinal) && context.Form.Messages.Count != 0);
        Assert.DoesNotContain("synthetic-ui-secret", log.Text);
        Assert.Contains("Назва документа", log.Text);
        Assert.Contains("Другий рядок.", Assert.Single(log.Lines, line => line.Contains("Маркер", StringComparison.Ordinal)));
        Assert.Equal(MessageBoxIcon.Warning, Assert.Single(context.Form.Messages).Icon);
        context.Form.Press("StartDownload");
        await context.FinishedAsync();
        Assert.StartsWith("Готово.", context.Form.Get<Label>("BatchStatus").Text);
    });

    private static void AssertLogColor(RichTextBox log, string text, Color expected)
    {
        var position = log.Text.IndexOf(text, StringComparison.Ordinal);
        Assert.True(position >= 0);
        log.Select(position, text.Length);
        Assert.Equal(expected.ToArgb(), log.SelectionColor.ToArgb());
    }
}
