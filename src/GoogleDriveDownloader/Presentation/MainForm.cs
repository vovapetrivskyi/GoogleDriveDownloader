using GoogleDriveDownloader.Application.Batch;
using GoogleDriveDownloader.Application.Logging;
using GoogleDriveDownloader.Infrastructure.Authentication;

namespace GoogleDriveDownloader.Presentation;

public partial class MainForm : Form
{
    private readonly ILoggingService _logging;
    private readonly OAuthConfigurationService _oauthConfiguration;
    private readonly GoogleAuthService _auth;
    private readonly BatchDownloadService _batch;
    private readonly CancellationTokenSource _lifetime = new();
    private Action<LogEntry>? _onLogEntry;
    private Action<AuthenticationStatus>? _onAuthStatus;
    private CancellationTokenSource? _operationCancellation;
    private Task _pendingTask = Task.CompletedTask;
    private bool _initialized;
    private bool _busy;
    private bool _downloading;
    private bool _closing;
    private bool _canClose;
    private int _batchGeneration;

    public MainForm(ILoggingService logging, OAuthConfigurationService oauthConfiguration, GoogleAuthService auth,
        BatchDownloadService batch)
    {
        _logging = logging;
        _oauthConfiguration = oauthConfiguration;
        _auth = auth;
        _batch = batch;
        InitializeComponent();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_initialized || _closing) return;
        _initialized = true;
        // Progress захоплює WindowsFormsSynchronizationContext саме в UI-потоці після створення вікна.
        IProgress<LogEntry> logs = new Progress<LogEntry>(entry =>
        {
            if (!CanUpdate) return;
            AppendLog(entry);
            if (entry.Context.Status == "FileLoggingUnavailable")
                ShowMessage(entry.Message, "Файловий журнал", MessageBoxIcon.Warning);
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
                await _auth.RestoreAsync(token);
            else if (CanUpdate)
            {
                const string message = "Імпортуйте власний OAuth Client типу Desktop через «Імпортувати OAuth JSON» або заповніть GoogleOAuth:ClientId і ClientSecret у appsettings.json поруч із EXE та перезапустіть програму.";
                _batchStatusLabel.Text = "Для початку імпортуйте OAuth Desktop JSON і увійдіть через Google.";
                _logging.Write(LogSeverity.Information, message, new LogContext(Status: "OAuthConfigurationRequired"));
            }
        });
    }

    private async void ImportOAuthButton_Click(object? sender, EventArgs e)
    {
        if (_busy || _closing) return;
        var path = SelectPath(ChooseOAuthFile);
        if (path is null) return;
        await RunOperationAsync(async token =>
        {
            // Спершу звільняємо in-memory credential; імпорт також змінює покоління кеша.
            await _auth.SignOutAsync(token);
            await _oauthConfiguration.ImportAsync(path, token);
        });
    }

    private async void SignInButton_Click(object? sender, EventArgs e) =>
        await RunOperationAsync(token => _auth.SignInAsync(token));

    private async void SignOutButton_Click(object? sender, EventArgs e) =>
        await RunOperationAsync(token => _auth.SignOutAsync(token));

    private async void ChangeAccountButton_Click(object? sender, EventArgs e) =>
        await RunOperationAsync(token => _auth.ChangeAccountAsync(token));

    private void CancelButton_Click(object? sender, EventArgs e)
    {
        if (!_busy || _closing) return;
        _cancelButton.Enabled = false;
        _batchStatusLabel.Text = "Скасування… Очікування завершення операції та очищення ресурсів.";
        _operationCancellation?.Cancel();
    }

    private Task RunOperationAsync(Func<CancellationToken, Task> operation, bool downloading = false)
    {
        if (_busy || _closing) return Task.CompletedTask;
        _busy = true;
        _downloading = downloading;
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        UpdateControls();
        _pendingTask = RunCoreAsync(operation, _operationCancellation, downloading);
        return _pendingTask;
    }

    private async Task RunCoreAsync(Func<CancellationToken, Task> operation, CancellationTokenSource cancellation, bool downloading)
    {
        try
        {
            await operation(cancellation.Token);
            if (!downloading && CanUpdate)
                _batchStatusLabel.Text = _auth.IsAuthorized ? "Оберіть XLSX і директорію та почніть завантаження."
                    : _oauthConfiguration.Current.IsConfigured ? "Увійдіть через Google, щоб завантажувати документи."
                    : "Для початку імпортуйте OAuth Desktop JSON і увійдіть через Google.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            var message = downloading ? "Скасовано." : "Операцію авторизації скасовано.";
            _logging.Write(LogSeverity.Information, message, new LogContext(Status: downloading ? "BatchCancelled" : "AuthorizationCancelled"));
            if (CanUpdate) _batchStatusLabel.Text = message;
        }
        catch (Exception exception) when (exception is AuthenticationException or AuthStorageException)
        {
            _logging.Write(LogSeverity.Warning, exception.Message, new LogContext(Status: "AuthorizationFailed"));
            if (CanUpdate)
            {
                _batchStatusLabel.Text = exception.Message;
                ShowMessage(exception.Message, "Google OAuth", MessageBoxIcon.Warning);
            }
        }
        catch (Exception)
        {
            // Вихідний виняток SDK може містити токени або URL із конфіденційними параметрами.
            var message = downloading ? "Не вдалося завершити завантаження. Перевірте журнал і повторіть спробу."
                : "Не вдалося завершити операцію авторизації. Повторіть спробу або перевірте налаштування OAuth.";
            _logging.Write(LogSeverity.Error, message, new LogContext(Status: downloading ? "BatchFailed" : "AuthorizationFailed"));
            if (CanUpdate)
            {
                _batchStatusLabel.Text = message;
                ShowMessage(message, "Google Drive Downloader", MessageBoxIcon.Error);
            }
        }
        finally
        {
            cancellation.Dispose();
            _operationCancellation = null;
            _busy = false;
            _downloading = false;
            if (CanUpdate)
            {
                _progressBar.Style = ProgressBarStyle.Blocks;
                UpdateControls();
            }
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
        var available = _initialized && !_busy && !_closing;
        _importOAuthButton.Enabled = available;
        _signInButton.Enabled = available && _oauthConfiguration.Current.IsConfigured && !_auth.IsAuthorized;
        _signOutButton.Enabled = available && _auth.IsAuthorized;
        _changeAccountButton.Enabled = available && _oauthConfiguration.Current.IsConfigured;
        _workbookTextBox.Enabled = available;
        _directoryTextBox.Enabled = available;
        _browseWorkbookButton.Enabled = available;
        _browseDirectoryButton.Enabled = available;
        _startButton.Enabled = available && _auth.IsAuthorized && !string.IsNullOrWhiteSpace(_workbookTextBox.Text)
            && !string.IsNullOrWhiteSpace(_directoryTextBox.Text);
        _cancelButton.Enabled = _busy && !_closing && _operationCancellation?.IsCancellationRequested == false;
    }

    private void AppendLog(LogEntry entry)
    {
        var row = entry.Context.RowNumber is { } number ? $"Рядок {number}: " : "";
        var document = entry.Context.DocumentName is { Length: > 0 } name ? name + " — " : "";
        var http = entry.Context.HttpStatusCode is { } code ? $" (HTTP {code})" : "";
        // Повідомлення вже очищено LoggingService. Переноси з назв не створюють підроблених рядків журналу.
        var text = $"[{entry.Timestamp:HH:mm:ss}] {row}{document}{entry.Message}{http}".Replace('\r', ' ').Replace('\n', ' ');
        _logBox.Select(_logBox.TextLength, 0);
        _logBox.SelectionColor = entry.Level == LogSeverity.Error ? Color.Firebrick
            : entry.Context.Status == "Succeeded" ? Color.ForestGreen : SystemColors.WindowText;
        _logBox.AppendText(text + Environment.NewLine);
        _logBox.SelectionColor = SystemColors.WindowText;
        _logBox.ScrollToCaret();
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
        UpdateControls();
        _batchStatusLabel.Text = "Завершення роботи… Очікування очищення ресурсів.";
        _lifetime.Cancel();
        // Message loop продовжує працювати, доки SDK та файловий сервіс звільняють свої ресурси.
        await _pendingTask;
        _canClose = true;
        if (!IsDisposed && !Disposing && IsHandleCreated) BeginInvoke(new Action(Close));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _logging.EntryLogged -= _onLogEntry;
            _auth.StatusChanged -= _onAuthStatus;
            _lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}
