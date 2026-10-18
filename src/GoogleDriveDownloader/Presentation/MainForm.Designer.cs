namespace GoogleDriveDownloader.Presentation;

partial class MainForm
{
    private Button _importOAuthButton = null!;
    private Button _signInButton = null!;
    private Button _signOutButton = null!;
    private Button _changeAccountButton = null!;
    private Button _cancelButton = null!;
    private Button _startButton = null!;
    private Button _browseWorkbookButton = null!;
    private Button _browseDirectoryButton = null!;
    private Label _oauthStatusLabel = null!;
    private Label _statisticsLabel = null!;
    private Label _batchStatusLabel = null!;
    private TextBox _workbookTextBox = null!;
    private TextBox _directoryTextBox = null!;
    private ProgressBar _progressBar = null!;
    private RichTextBox _logBox = null!;

    private void InitializeComponent()
    {
        SuspendLayout();
        _oauthStatusLabel = new Label { Name = "OAuthStatus", AutoSize = true, Dock = DockStyle.Fill, Text = "Не авторизовано" };
        _importOAuthButton = new Button { Name = "ImportOAuth", AutoSize = true, Text = "Імпортувати OAuth JSON", Enabled = false };
        _signInButton = new Button { Name = "SignIn", AutoSize = true, Text = "Увійти через Google", Enabled = false };
        _signOutButton = new Button { Name = "SignOut", AutoSize = true, Text = "Вийти", Enabled = false };
        _changeAccountButton = new Button { Name = "ChangeAccount", AutoSize = true, Text = "Змінити акаунт", Enabled = false };
        _cancelButton = new Button { Name = "CancelOperation", AutoSize = true, Text = "Скасувати", Enabled = false };
        _startButton = new Button { Name = "StartDownload", AutoSize = true, Text = "Почати завантаження", Enabled = false };
        _browseWorkbookButton = new Button { Name = "BrowseWorkbook", AutoSize = true, Text = "Огляд…", Enabled = false };
        _browseDirectoryButton = new Button { Name = "BrowseDirectory", AutoSize = true, Text = "Огляд…", Enabled = false };
        _workbookTextBox = new TextBox { Name = "WorkbookPath", Dock = DockStyle.Fill, Enabled = false, PlaceholderText = "Оберіть файл .xlsx" };
        _directoryTextBox = new TextBox { Name = "DirectoryPath", Dock = DockStyle.Fill, Enabled = false, PlaceholderText = "Оберіть директорію збереження" };
        _progressBar = new ProgressBar { Name = "DownloadProgress", Dock = DockStyle.Fill, Height = 20 };
        _statisticsLabel = new Label { Name = "Statistics", AutoSize = true, Dock = DockStyle.Fill, Text = "Оброблено: 0 / 0. Успішно: 0, помилок: 0, пропущено: 0." };
        _batchStatusLabel = new Label { Name = "BatchStatus", AutoSize = true, Dock = DockStyle.Fill, Text = "Завантаження ще не розпочато." };
        _logBox = new RichTextBox
        {
            Name = "Log", Dock = DockStyle.Fill, ReadOnly = true, DetectUrls = false,
            ScrollBars = RichTextBoxScrollBars.Vertical, BackColor = SystemColors.Window, HideSelection = false
        };

        var authButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        authButtons.Controls.AddRange([_importOAuthButton, _signInButton, _signOutButton, _changeAccountButton]);
        var paths = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = Padding.Empty };
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        paths.Controls.Add(new Label { AutoSize = true, Text = "Файл Excel:", Anchor = AnchorStyles.Left }, 0, 0);
        paths.Controls.Add(_workbookTextBox, 1, 0);
        paths.Controls.Add(_browseWorkbookButton, 2, 0);
        paths.Controls.Add(new Label { AutoSize = true, Text = "Зберегти в:", Anchor = AnchorStyles.Left }, 0, 1);
        paths.Controls.Add(_directoryTextBox, 1, 1);
        paths.Controls.Add(_browseDirectoryButton, 2, 1);
        var execution = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        execution.Controls.AddRange([_startButton, _cancelButton]);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 9
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 8; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(_oauthStatusLabel, 0, 0);
        layout.Controls.Add(authButtons, 0, 1);
        layout.Controls.Add(paths, 0, 2);
        layout.Controls.Add(execution, 0, 3);
        layout.Controls.Add(_progressBar, 0, 4);
        layout.Controls.Add(_statisticsLabel, 0, 5);
        layout.Controls.Add(_batchStatusLabel, 0, 6);
        layout.Controls.Add(new Label { AutoSize = true, Text = "Журнал:", Dock = DockStyle.Fill }, 0, 7);
        layout.Controls.Add(_logBox, 0, 8);

        _importOAuthButton.Click += ImportOAuthButton_Click;
        _signInButton.Click += SignInButton_Click;
        _signOutButton.Click += SignOutButton_Click;
        _changeAccountButton.Click += ChangeAccountButton_Click;
        _cancelButton.Click += CancelButton_Click;
        _startButton.Click += StartButton_Click;
        _browseWorkbookButton.Click += BrowseWorkbookButton_Click;
        _browseDirectoryButton.Click += BrowseDirectoryButton_Click;
        _workbookTextBox.TextChanged += Paths_TextChanged;
        _directoryTextBox.TextChanged += Paths_TextChanged;
        Controls.Add(layout);
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(700, 500);
        MinimumSize = new Size(660, 480);
        Name = "MainForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Google Drive Downloader";
        ResumeLayout(false);
        PerformLayout();
    }
}
