namespace GoogleDriveDownloader.Presentation;

partial class MainForm
{
    private Button _importOAuthButton = null!;
    private Button _signInButton = null!;
    private Button _signOutButton = null!;
    private Button _changeAccountButton = null!;
    private Button _cancelAuthButton = null!;
    private Label _oauthStatusLabel = null!;

    private void InitializeComponent()
    {
        SuspendLayout();
        _oauthStatusLabel = new Label { AutoSize = true, Location = new Point(16, 18), Text = "Не авторизовано" };
        _importOAuthButton = new Button { AutoSize = true, Location = new Point(16, 48), Text = "Імпортувати OAuth JSON", Enabled = false };
        _signInButton = new Button { AutoSize = true, Location = new Point(16, 88), Text = "Увійти через Google", Enabled = false };
        _signOutButton = new Button { AutoSize = true, Location = new Point(190, 88), Text = "Вийти", Enabled = false };
        _changeAccountButton = new Button { AutoSize = true, Location = new Point(275, 88), Text = "Змінити акаунт", Enabled = false };
        _cancelAuthButton = new Button { AutoSize = true, Location = new Point(420, 88), Text = "Скасувати вхід", Enabled = false };
        _importOAuthButton.Click += ImportOAuthButton_Click;
        _signInButton.Click += SignInButton_Click;
        _signOutButton.Click += SignOutButton_Click;
        _changeAccountButton.Click += ChangeAccountButton_Click;
        _cancelAuthButton.Click += CancelAuthButton_Click;
        Controls.AddRange([_oauthStatusLabel, _importOAuthButton, _signInButton, _signOutButton, _changeAccountButton, _cancelAuthButton]);
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(700, 500);
        Name = "MainForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Google Drive Downloader";
        ResumeLayout(false);
        PerformLayout();
    }
}
