using GoogleDriveDownloader.Application.Configuration;
using GoogleDriveDownloader.Application.Logging;

namespace GoogleDriveDownloader.Presentation;

public partial class MainForm : Form
{
    private readonly AppSettings _settings;
    private readonly ILoggingService _logging;
    private Action<LogEntry>? _onLogEntry;

    public MainForm(AppSettings settings, ILoggingService logging)
    {
        _settings = settings;
        _logging = logging;
        InitializeComponent();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        IProgress<LogEntry> progress = new Progress<LogEntry>(entry =>
        {
            if (!IsDisposed && !Disposing && entry.Context.Status == "FileLoggingUnavailable")
            {
                MessageBox.Show(this, entry.Message, "Файловий журнал", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        });
        _onLogEntry = progress.Report;
        _logging.EntryLogged += _onLogEntry;
        _logging.Write(LogSeverity.Information, "Застосунок запущено.", new LogContext(Status: "Started"));
        if (!_settings.GoogleOAuth.IsConfigured)
        {
            const string message = "Google OAuth ще не налаштовано. У зовнішньому appsettings.json поруч із EXE заповніть GoogleOAuth:ClientId і GoogleOAuth:ClientSecret даними власного OAuth Client типу Desktop. Після зміни конфігурації перезапустіть програму.";
            _logging.Write(LogSeverity.Information, message, new LogContext(Status: "OAuthNotConfigured"));
            MessageBox.Show(this, message, "Налаштування Google OAuth", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _logging.EntryLogged -= _onLogEntry;
        base.OnFormClosed(e);
    }
}
