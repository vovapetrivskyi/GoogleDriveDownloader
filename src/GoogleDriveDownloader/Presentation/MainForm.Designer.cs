namespace GoogleDriveDownloader.Presentation;

partial class MainForm
{
    private Button _importOAuthButton = null!;
    private Label _oauthStatusLabel = null!;

    private void InitializeComponent()
    {
        SuspendLayout();
        _oauthStatusLabel = new Label
        {
            AutoSize = true,
            Location = new Point(16, 18),
            Text = "Не авторизовано"
        };
        _importOAuthButton = new Button
        {
            AutoSize = true,
            Location = new Point(16, 48),
            Text = "Імпортувати OAuth JSON",
            Enabled = false
        };
        _importOAuthButton.Click += ImportOAuthButton_Click;
        Controls.Add(_oauthStatusLabel);
        Controls.Add(_importOAuthButton);
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(700, 500);
        Name = "MainForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Google Drive Downloader";
        ResumeLayout(false);
    }
}
