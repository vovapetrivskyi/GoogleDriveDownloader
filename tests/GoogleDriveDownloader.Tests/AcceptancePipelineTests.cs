using System.Net;
using System.Text.Json;
using GoogleDriveDownloader.Application.Batch;
using GoogleDriveDownloader.Application.Configuration;
using GoogleDriveDownloader.Application.Input;
using GoogleDriveDownloader.Application.Storage;
using GoogleDriveDownloader.Infrastructure.Authentication;
using GoogleDriveDownloader.Infrastructure.Drive;
using GoogleDriveDownloader.Infrastructure.Storage;

namespace GoogleDriveDownloader.Tests;

public sealed class AcceptancePipelineTests
{
    [Fact]
    public async Task PersistedSampleRunsThroughOAuthSdkAllThreeExportsStorageAndSanitizedFileLog()
    {
        using var output = new TestDirectory();
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        using var oauth = new OAuthTestTransport();
        await using var auth = new GoogleAuthService(scope.Settings, scope.Configuration, scope.Logging, () => new OAuthTestReceiver(), oauth);
        await auth.SignInAsync();
        using var transport = new DriveTestTransport { RespondAsync = (request, _) => Task.FromResult(Respond(request.RequestUri!)) };
        using var drive = new GoogleDriveService(scope.Settings, auth, transport);
        var batch = Batch(scope, drive);

        var result = await batch.RunAsync(SampleWorkbookTests.Sample("downloads.xlsx"), output.Path);
        await scope.Logging.DisposeAsync();

        Assert.Equal(BatchCompletionStatus.Completed, result.Status);
        Assert.Equal((5, 5, 5, 0, 0), (result.Total, result.Processed, result.Succeeded, result.Failed, result.Skipped));
        var expected = new[] { "Звіт.pdf", "Документ.docx", "Таблиця.xlsx", "Презентація.pptx", "Звіт (2).pdf" };
        Assert.Equal(expected, result.Results.Select(row => Path.GetFileName(row.FilePath)));
        foreach (var path in Directory.GetFiles(output.Path)) Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(path));
        Assert.Equal(5, Directory.GetFiles(output.Path).Length);
        Assert.Empty(Directory.GetFiles(output.Path, "*.part"));
        Assert.Single(oauth.Requests);
        Assert.Equal(10, transport.Requests.Count);
        Assert.All(transport.Requests, request => Assert.Equal("Bearer synthetic-access-new", request.Authorization));
        Assert.Equal(new[] { ExportSettings.Docx, ExportSettings.Xlsx, ExportSettings.Pptx }, transport.Requests
            .Where(request => request.Uri.AbsolutePath.EndsWith("/export", StringComparison.Ordinal))
            .Select(request => OAuthTestTransport.Parse(request.Uri.Query)["mimeType"]));
        Assert.All(transport.Requests.Where(request => request.Uri.AbsolutePath.Contains("SYNTHETIC_DOC_001", StringComparison.Ordinal)),
            request => Assert.Equal("SYNTHETIC_DOC_001/SYNTHETIC_KEY_001", request.ResourceKeys));
        var lines = await File.ReadAllLinesAsync(Assert.Single(Directory.GetFiles(Path.Combine(scope.UserDirectory, "logs"), "*.jsonl")));
        var published = scope.Entries.ToArray();
        Assert.Equal(published.Length, lines.Length);
        Assert.Equal(5, published.Count(entry => entry.Context.Status == "Succeeded"));
        for (var index = 0; index < lines.Length; index++)
        {
            using var json = JsonDocument.Parse(lines[index]);
            Assert.Equal(published[index].Message, json.RootElement.GetProperty("Properties").GetProperty("Message").GetString());
        }
        var logs = string.Join('\n', lines) + string.Join('\n', published.Select(entry => entry.ToString()));
        foreach (var marker in new[] { "synthetic-access-new", "synthetic-refresh-new", scope.Settings.GoogleOAuth.ClientId,
            scope.Settings.GoogleOAuth.ClientSecret, "SYNTHETIC_KEY_001", "DO-NOT-EXPOSE" })
            Assert.DoesNotContain(marker, logs);
    }

    [Fact]
    public async Task RepeatedSdk401StopsWholeBatchAndExplicitSignInAllowsCompleteNewRun()
    {
        using var output = new TestDirectory();
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        using var oauth = new OAuthTestTransport();
        await using var auth = new GoogleAuthService(scope.Settings, scope.Configuration, scope.Logging, () => new OAuthTestReceiver(), oauth);
        await auth.SignInAsync();
        using var transport = new DriveTestTransport
        {
            RespondAsync = (_, _) => Task.FromResult(DriveTestTransport.Error(HttpStatusCode.Unauthorized))
        };
        using var drive = new GoogleDriveService(scope.Settings, auth, transport);
        var batch = Batch(scope, drive);
        var first = await batch.RunAsync(SampleWorkbookTests.Sample("downloads.xlsx"), output.Path);
        Assert.Equal(BatchCompletionStatus.Failed, first.Status);
        Assert.Equal((0, 1, 4), (first.Succeeded, first.Failed, first.Skipped));
        Assert.Equal("AuthenticationRequired", first.Results[0].ErrorCode);
        Assert.False(auth.IsAuthorized);
        Assert.False(File.Exists(scope.TokenPath));
        Assert.Equal(2, transport.Requests.Count);
        Assert.Equal(2, oauth.Requests.Count);
        Assert.Empty(Directory.GetFiles(output.Path));

        await auth.SignInAsync();
        transport.RespondAsync = (request, _) => Task.FromResult(Respond(request.RequestUri!));
        var second = await batch.RunAsync(SampleWorkbookTests.Sample("downloads.xlsx"), output.Path);
        Assert.Equal(BatchCompletionStatus.Completed, second.Status);
        Assert.Equal(5, second.Succeeded);
        Assert.True(auth.IsAuthorized);
    }

    [Fact]
    public async Task CancellationOfActualSdkBodyRemovesPartAndSkipsEveryRemainingSampleRow()
    {
        using var output = new TestDirectory();
        await using var scope = new AuthTestScope(configured: true);
        await scope.Configuration.LoadAsync();
        using var oauth = new OAuthTestTransport();
        await using var auth = new GoogleAuthService(scope.Settings, scope.Configuration, scope.Logging, () => new OAuthTestReceiver(), oauth);
        await auth.SignInAsync();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = new DriveSourceStream(1024 * 1024) { FailAfter = 8192, Blocked = blocked };
        using var transport = new DriveTestTransport
        {
            RespondAsync = (request, _) => Task.FromResult(request.RequestUri!.Query.Contains("alt=media", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) }
                : DriveTestTransport.Metadata("SYNTHETIC_PDF_001", size: 1024 * 1024))
        };
        using var drive = new GoogleDriveService(scope.Settings, auth, transport);
        using var cancellation = new CancellationTokenSource();
        var pending = Batch(scope, drive).RunAsync(SampleWorkbookTests.Sample("downloads.xlsx"), output.Path, cancellationToken: cancellation.Token);
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(8));
        cancellation.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(BatchCompletionStatus.Cancelled, result.Status);
        Assert.Equal((0, 0, 5), (result.Succeeded, result.Failed, result.Skipped));
        Assert.Equal(new[] { 1, 0, 0, 0, 0 }, result.Results.Select(row => row.Attempts));
        Assert.True(source.Disposed);
        Assert.Equal(2, transport.Requests.Count);
        Assert.Empty(Directory.GetFiles(output.Path));
    }

    private static BatchDownloadService Batch(AuthTestScope scope, GoogleDriveService drive)
    {
        var names = new FileNameResolver();
        return new BatchDownloadService(new ExcelReader(new DriveLinkParser()), drive, names,
            new FileStorageService(names, scope.Logging), new BatchTestRetry(), scope.Logging);
    }

    private static HttpResponseMessage Respond(Uri uri)
    {
        if (uri.AbsolutePath.EndsWith("/export", StringComparison.Ordinal) || uri.Query.Contains("alt=media", StringComparison.Ordinal))
            return DriveTestTransport.Content([1, 2, 3, 4]);
        var id = uri.AbsolutePath.Split('/')[^1];
        var mime = id switch
        {
            "SYNTHETIC_DOC_001" => "application/vnd.google-apps.document",
            "SYNTHETIC_SHEET_001" => "application/vnd.google-apps.spreadsheet",
            "SYNTHETIC_SLIDE_001" => "application/vnd.google-apps.presentation",
            _ => "application/pdf"
        };
        return DriveTestTransport.Metadata(id, mime, mime == "application/pdf" ? "pdf" : null, mime == "application/pdf" ? 4 : null);
    }
}
