using GoogleDriveDownloader.Application.Batch;
using GoogleDriveDownloader.Application.Logging;

namespace GoogleDriveDownloader.Presentation;

public partial class MainForm
{
    private void BrowseWorkbookButton_Click(object? sender, EventArgs e)
    {
        if (_busy || _closing) return;
        if (SelectPath(ChooseWorkbookFile) is { } path) _workbookTextBox.Text = path;
    }

    private void BrowseDirectoryButton_Click(object? sender, EventArgs e)
    {
        if (_busy || _closing) return;
        if (SelectPath(ChooseDestinationDirectory) is { } path) _directoryTextBox.Text = path;
    }

    private void Paths_TextChanged(object? sender, EventArgs e)
    {
        if (_initialized && CanUpdate) UpdateControls();
    }

    private async void StartButton_Click(object? sender, EventArgs e)
    {
        if (_busy || _closing) return;
        if (!_auth.IsAuthorized)
        {
            ShowMessage("Спочатку увійдіть через Google.", "Завантаження", MessageBoxIcon.Information);
            return;
        }
        // Фіксуємо параметри на весь пакет; файлові перевірки можуть звертатись до SMB і виконуються у фоні.
        var workbookPath = _workbookTextBox.Text.Trim();
        var directory = _directoryTextBox.Text.Trim();
        await RunOperationAsync(async token =>
        {
            ResetDownloadProgress();
            var validation = await Task.Run(() => ValidatePaths(workbookPath, directory, token), token);
            token.ThrowIfCancellationRequested();
            if (validation is not null)
            {
                if (!CanUpdate) return;
                _progressBar.Style = ProgressBarStyle.Blocks;
                _batchStatusLabel.Text = validation;
                _logging.Write(LogSeverity.Warning, validation, new LogContext(Status: "InvalidBatchInput"));
                ShowMessage(validation, "Вхідні дані", MessageBoxIcon.Warning);
                return;
            }
            var generation = ++_batchGeneration;
            IProgress<DownloadProgress> progress = new Progress<DownloadProgress>(value =>
            {
                // Повідомлення, вже поставлене в UI-чергу, не повинно змінити підсумок або наступний пакет.
                if (CanUpdate && _downloading && generation == _batchGeneration) ApplyProgress(value);
            });
            var result = await _batch.RunAsync(workbookPath, directory, progress, token);
            if (!CanUpdate) return;
            ++_batchGeneration;
            ApplyProgress(new DownloadProgress(result.Total, result.Processed, result.Succeeded, result.Failed,
                result.Skipped, CompletionStatus: result.Status));
            _batchStatusLabel.Text = result.Message;
        }, downloading: true);
    }

    private static string? ValidatePaths(string workbookPath, string directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(workbookPath) || !Path.GetExtension(workbookPath).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            return "Оберіть вхідний файл Excel у форматі .xlsx.";
        if (!File.Exists(workbookPath)) return "Вхідний файл XLSX недоступний. Перевірте шлях і права доступу.";
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return "Оберіть наявну доступну директорію збереження.";
        token.ThrowIfCancellationRequested();
        return null;
    }

    private void ResetDownloadProgress()
    {
        _progressBar.Value = 0;
        _progressBar.Maximum = 1;
        _progressBar.Style = ProgressBarStyle.Marquee;
        _statisticsLabel.Text = "Оброблено: 0 / 0. Успішно: 0, помилок: 0, пропущено: 0.";
        _batchStatusLabel.Text = "Читання XLSX…";
    }

    private void ApplyProgress(DownloadProgress value)
    {
        _progressBar.Style = ProgressBarStyle.Blocks;
        var maximum = Math.Max(1, value.Total);
        if (_progressBar.Value > maximum) _progressBar.Value = 0;
        _progressBar.Maximum = maximum;
        _progressBar.Value = Math.Clamp(value.Processed, 0, _progressBar.Maximum);
        _statisticsLabel.Text = $"Оброблено: {value.Processed} / {value.Total}. Успішно: {value.Succeeded}, помилок: {value.Failed}, пропущено: {value.Skipped}.";
        _batchStatusLabel.Text = value.CompletionStatus switch
        {
            BatchCompletionStatus.Completed => "Готово.",
            BatchCompletionStatus.Cancelled => "Скасовано.",
            BatchCompletionStatus.Failed => "Пакет зупинено через критичну помилку.",
            _ => _operationCancellation?.IsCancellationRequested == true ? "Скасування…" : "Завантаження…"
        };
    }
}
