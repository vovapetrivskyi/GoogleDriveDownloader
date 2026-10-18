using GoogleDriveDownloader.Application.Logging;
using GoogleDriveDownloader.Infrastructure.Authentication;

namespace GoogleDriveDownloader.Presentation;

public partial class MainForm : Form
{
    private readonly ILoggingService _logging;
    private readonly OAuthConfigurationService _oauthConfiguration;
    private readonly CancellationTokenSource _lifetime = new();
    private Action<LogEntry>? _onLogEntry;

    public MainForm(ILoggingService logging, OAuthConfigurationService oauthConfiguration)
    {
        _logging = logging;
        _oauthConfiguration = oauthConfiguration;
        InitializeComponent();
    }

    protected override async void OnShown(EventArgs e)
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
        try
        {
            await _oauthConfiguration.LoadAsync(_lifetime.Token);
            if (IsDisposed || Disposing)
            {
                return;
            }

            UpdateOAuthStatus();
            if (!_oauthConfiguration.Current.IsConfigured)
            {
                const string message = "Google OAuth не налаштовано або імпортований файл недоступний. Натисніть «Імпортувати OAuth JSON» і оберіть файл власного OAuth Client типу Desktop. Також можна заповнити GoogleOAuth:ClientId і ClientSecret у appsettings.json поруч із EXE; зміни цього файла застосовуються після перезапуску.";
                _logging.Write(LogSeverity.Information, message, new LogContext(Status: "OAuthNotConfigured"));
                MessageBox.Show(this, message, "Налаштування Google OAuth", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!IsDisposed && !Disposing)
            {
                _importOAuthButton.Enabled = true;
            }
        }
    }

    private async void ImportOAuthButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "OAuth Desktop JSON (*.json)|*.json",
            CheckFileExists = true,
            Title = "Оберіть OAuth Desktop JSON із Google Cloud Console"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _importOAuthButton.Enabled = false;
        try
        {
            await _oauthConfiguration.ImportAsync(dialog.FileName, _lifetime.Token);
            if (!IsDisposed && !Disposing)
            {
                UpdateOAuthStatus();
                MessageBox.Show(this, "OAuth Desktop Client імпортовано. Попередні локальні токени видалено; потрібна нова авторизація.",
                    "Google OAuth", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (AuthStorageException exception)
        {
            if (!IsDisposed && !Disposing)
            {
                MessageBox.Show(this, exception.Message, "Помилка імпорту OAuth", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            if (!IsDisposed && !Disposing)
            {
                _importOAuthButton.Enabled = true;
            }
        }
    }

    private void UpdateOAuthStatus() => _oauthStatusLabel.Text = _oauthConfiguration.Current.IsConfigured
        ? "Не авторизовано. OAuth-конфігурацію налаштовано."
        : "Не авторизовано. OAuth-конфігурацію не налаштовано.";

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (!e.Cancel)
        {
            _lifetime.Cancel();
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _logging.EntryLogged -= _onLogEntry;
        _lifetime.Dispose();
        base.OnFormClosed(e);
    }
}
