using System.ComponentModel;
using System.Windows.Forms;
using ClosedXML.Excel;
using GoogleDriveDownloader.Application.Batch;
using GoogleDriveDownloader.Application.Input;
using GoogleDriveDownloader.Application.Logging;
using GoogleDriveDownloader.Application.Storage;
using GoogleDriveDownloader.Infrastructure.Authentication;
using GoogleDriveDownloader.Infrastructure.Storage;
using GoogleDriveDownloader.Presentation;

namespace GoogleDriveDownloader.Tests;

internal sealed class MainFormTestContext : IAsyncDisposable
{
    private readonly TestDirectory _files = new();
    private readonly OAuthTestTransport _transport = new();
    public AuthTestScope Scope { get; }
    public GoogleAuthService Auth { get; }
    public OAuthTestReceiver Receiver { get; } = new();
    public BatchTestDrive Drive { get; } = new();
    public BatchTestRetry Retry { get; } = new();
    public TestMainForm Form { get; }
    public string WorkbookPath { get; }
    public string OutputDirectory { get; }

    public MainFormTestContext(bool configured = true)
    {
        Scope = new AuthTestScope(configured);
        Auth = new GoogleAuthService(Scope.Settings, Scope.Configuration, Scope.Logging, () => Receiver, _transport);
        var names = new FileNameResolver();
        var batch = new BatchDownloadService(new ExcelReader(new DriveLinkParser()), Drive, names,
            new FileStorageService(names, Scope.Logging), Retry, Scope.Logging);
        Form = new TestMainForm(Scope.Logging, Scope.Configuration, Auth, batch);
        WorkbookPath = Path.Combine(_files.Path, "input.xlsx");
        OutputDirectory = Directory.CreateDirectory(Path.Combine(_files.Path, "output")).FullName;
    }

    public async Task OpenAsync(bool authorize = true, int rows = 3)
    {
        await Scope.Configuration.LoadAsync();
        if (authorize) await Auth.SignInAsync();
        WriteWorkbook(rows);
        Form.Show();
        await ReadyAsync();
        Form.WorkbookChoice = WorkbookPath;
        Form.DirectoryChoice = OutputDirectory;
        Form.Press("BrowseWorkbook");
        Form.Press("BrowseDirectory");
    }

    public void WriteWorkbook(int count, bool invalidLast = false)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Дані");
        sheet.Cell(1, 1).Value = "Назва";
        sheet.Cell(1, 2).Value = "Посилання";
        for (var row = 2; row < count + 2; row++)
        {
            sheet.Cell(row, 1).Value = "Документ " + row;
            sheet.Cell(row, 2).Value = invalidLast && row == count + 1 ? "https://example.org/private"
                : "https://drive.google.com/file/d/File_" + row + "/view";
        }
        workbook.SaveAs(WorkbookPath);
    }

    public Task ReadyAsync() => WinFormsTestHost.UntilAsync(() => Form.Get<Button>("ImportOAuth").Enabled);
    public Task FinishedAsync() => WinFormsTestHost.UntilAsync(() => Form.Get<Button>("StartDownload").Enabled);

    public async ValueTask DisposeAsync()
    {
        if (!Form.IsDisposed)
        {
            Form.Close();
            await WinFormsTestHost.UntilAsync(() => Form.IsDisposed);
        }
        await Auth.DisposeAsync();
        _transport.Dispose();
        await Scope.DisposeAsync();
        _files.Dispose();
    }
}

internal sealed class TestMainForm(ILoggingService logging, OAuthConfigurationService configuration, GoogleAuthService auth,
    BatchDownloadService batch) : MainForm(logging, configuration, auth, batch)
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string? WorkbookChoice { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string? DirectoryChoice { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string? OAuthChoice { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool FailDialog { get; set; }
    public List<(string Message, string Title, MessageBoxIcon Icon)> Messages { get; } = [];
    public T Get<T>(string name) where T : Control => Assert.IsType<T>(Assert.Single(Controls.Find(name, true)));
    public void Press(string name) => Get<Button>(name).PerformClick();
    protected override string? ChooseWorkbookFile() => FailDialog ? throw new IOException("DO-NOT-EXPOSE dialog-error") : WorkbookChoice;
    protected override string? ChooseDestinationDirectory() => DirectoryChoice;
    protected override string? ChooseOAuthFile() => OAuthChoice;
    protected override void ShowMessage(string message, string title, MessageBoxIcon icon) => Messages.Add((message, title, icon));
    protected override void OnLoad(EventArgs e)
    {
        Opacity = 0;
        ShowInTaskbar = false;
        base.OnLoad(e);
    }
}
