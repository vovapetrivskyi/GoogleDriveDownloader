using GoogleDriveDownloader.Application.Logging;
using GoogleDriveDownloader.Infrastructure.Authentication;

namespace GoogleDriveDownloader.Presentation;

public partial class MainForm : Form
{
    private readonly ILoggingService _logging;
    private readonly OAuthConfigurationService _oauthConfiguration;
    private readonly GoogleAuthService _auth;
    private readonly CancellationTokenSource _lifetime = new();
    private Action<LogEntry>? _onLogEntry;
    private Action<AuthenticationStatus>? _onAuthStatus;
    private CancellationTokenSource? _operationCancellation;
    private Task _pendingTask = Task.CompletedTask;
    private bool _busy;
    private bool _closing;
    private bool _canClose;

    public MainForm(ILoggingService logging, OAuthConfigurationService oauthConfiguration, GoogleAuthService auth)
    {
        _logging = logging;
        _oauthConfiguration = oauthConfiguration;
        _auth = auth;
        InitializeComponent();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        IProgress<LogEntry> logs = new Progress<LogEntry>(entry =>
        {
            if (CanUpdate && entry.Context.Status == "FileLoggingUnavailable")
                MessageBox.Show(this, entry.Message, "Файловий журнал", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        });
        IProgress<AuthenticationStatus> auth = new Progress<AuthenticationStatus>(_ =>
        {
            if (CanUpdate) UpdateControls();
        });
        _onLogEntry = logs.Report;
        _onAuthStatus = auth.Report;
        _logging.EntryLogged += _onLogEntry;
        _auth.StatusChanged += _onAuthStatus;
        _logging.Write(LogSeverity.Information, "Застосунок запущено.", new LogContext(Status: "Started"));
        await RunOperationAsync(async token =>
        {
            await _oauthConfiguration.LoadAsync(token);
            if (_oauthConfiguration.Current.IsConfigured)
            {
                await _auth.RestoreAsync(token);
            }
            else if (CanUpdate)
            {
                MessageBox.Show(this,
                    "Імпортуйте власний OAuth Client типу Desktop через «Імпортувати OAuth JSON» або заповніть GoogleOAuth:ClientId і ClientSecret у appsettings.json поруч із EXE та перезапустіть програму.",
                    "Налаштування Google OAuth", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        });
    }

    private async void ImportOAuthButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "OAuth Desktop JSON (*.json)|*.json", CheckFileExists = true,
            Title = "Оберіть OAuth Desktop JSON із Google Cloud Console"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await RunOperationAsync(async token =>
        {
            // Спершу звільняємо in-memory credential; імпорт також змінює покоління кеша.
            await _auth.SignOutAsync(token);
            await _oauthConfiguration.ImportAsync(dialog.FileName, token);
        });
    }

    private async void SignInButton_Click(object? sender, EventArgs e) =>
        await RunOperationAsync(token => _auth.SignInAsync(token));

    private async void SignOutButton_Click(object? sender, EventArgs e) =>
        await RunOperationAsync(token => _auth.SignOutAsync(token));

    private async void ChangeAccountButton_Click(object? sender, EventArgs e) =>
        await RunOperationAsync(token => _auth.ChangeAccountAsync(token));

    private void CancelAuthButton_Click(object? sender, EventArgs e) => _operationCancellation?.Cancel();

    private Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (_busy || _closing) return Task.CompletedTask;
        _busy = true;
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        UpdateControls();
        _pendingTask = RunCoreAsync(operation, _operationCancellation);
        return _pendingTask;
    }

    private async Task RunCoreAsync(Func<CancellationToken, Task> operation, CancellationTokenSource cancellation)
    {
        try
        {
            await operation(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            _logging.Write(LogSeverity.Information, "Операцію авторизації скасовано.", new LogContext(Status: "AuthorizationCancelled"));
        }
        catch (Exception exception) when (exception is AuthenticationException or AuthStorageException)
        {
            _logging.Write(LogSeverity.Warning, exception.Message, new LogContext(Status: "AuthorizationFailed"));
            if (CanUpdate)
                MessageBox.Show(this, exception.Message, "Google OAuth", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception)
        {
            // Вихідний виняток SDK може містити токени або URL із конфіденційними параметрами.
            const string message = "Не вдалося завершити операцію авторизації. Повторіть спробу або перевірте налаштування OAuth.";
            _logging.Write(LogSeverity.Error, message, new LogContext(Status: "AuthorizationFailed"));
            if (CanUpdate) MessageBox.Show(this, message, "Google OAuth", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            cancellation.Dispose();
            _operationCancellation = null;
            _busy = false;
            if (CanUpdate) UpdateControls();
        }
    }

    private bool CanUpdate => !IsDisposed && !Disposing && !_closing;

    private void UpdateControls()
    {
        _oauthStatusLabel.Text = _auth.Status switch
        {
            AuthenticationStatus.Authorized => "Авторизовано",
            AuthenticationStatus.AuthorizationRequired => "Не авторизовано. Потрібен повторний вхід через Google.",
            _ => _oauthConfiguration.Current.IsConfigured ? "Не авторизовано. OAuth налаштовано." : "Не авторизовано. Імпортуйте OAuth Desktop JSON."
        };
        _importOAuthButton.Enabled = !_busy;
        _signInButton.Enabled = !_busy && _oauthConfiguration.Current.IsConfigured && !_auth.IsAuthorized;
        _signOutButton.Enabled = !_busy && _auth.IsAuthorized;
        _changeAccountButton.Enabled = !_busy && _oauthConfiguration.Current.IsConfigured;
        _cancelAuthButton.Enabled = _busy;
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (_canClose || e.Cancel) return;
        if (_pendingTask.IsCompleted)
        {
            _closing = true;
            _lifetime.Cancel();
            return;
        }
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _lifetime.Cancel();
        // Message loop продовжує працювати, доки SDK закриває loopback receiver і файлові операції.
        await _pendingTask;
        _canClose = true;
        if (!IsDisposed) BeginInvoke(new Action(Close));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _logging.EntryLogged -= _onLogEntry;
        _auth.StatusChanged -= _onAuthStatus;
        _lifetime.Dispose();
        base.OnFormClosed(e);
    }
}
