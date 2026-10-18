using GoogleDriveDownloader.Application.Logging;

namespace GoogleDriveDownloader.Presentation;

public partial class MainForm
{
    protected virtual string? ChooseOAuthFile()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "OAuth Desktop JSON (*.json)|*.json", CheckFileExists = true,
            Title = "Оберіть OAuth Desktop JSON із Google Cloud Console"
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    protected virtual string? ChooseWorkbookFile()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Excel (*.xlsx)|*.xlsx", CheckFileExists = true, Multiselect = false,
            Title = "Оберіть XLSX із назвами та посиланнями Google Drive"
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    protected virtual string? ChooseDestinationDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Оберіть директорію збереження документів", UseDescriptionForTitle = true
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    protected virtual void ShowMessage(string message, string title, MessageBoxIcon icon) =>
        MessageBox.Show(this, message, title, MessageBoxButtons.OK, icon);

    private string? SelectPath(Func<string?> select)
    {
        try { return select(); }
        catch (Exception)
        {
            const string message = "Не вдалося відкрити діалог вибору. Спробуйте ще раз або введіть шлях вручну.";
            _logging.Write(LogSeverity.Warning, message, new LogContext(Status: "FileDialogUnavailable"));
            if (CanUpdate) ShowMessage(message, "Вибір файла або директорії", MessageBoxIcon.Warning);
            return null;
        }
    }
}
