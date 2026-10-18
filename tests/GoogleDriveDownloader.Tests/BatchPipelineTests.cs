using System.Net;
using ClosedXML.Excel;
using GoogleDriveDownloader.Application.Batch;
using GoogleDriveDownloader.Application.Input;
using GoogleDriveDownloader.Application.Storage;
using GoogleDriveDownloader.Infrastructure.Storage;

namespace GoogleDriveDownloader.Tests;

public sealed class BatchPipelineTests
{
    [Fact]
    public async Task WorkbookSdkStoragePipelineRetriesPartialContentAndContinuesAfterRowErrors()
    {
        using var directory = new TestDirectory();
        var input = Path.Combine(directory.Path, "input.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.AddWorksheet("Вхідні дані");
            sheet.Cell(1, 1).Value = "Назва";
            sheet.Cell(1, 2).Value = "Посилання";
            sheet.Cell(2, 1).Value = "Звіт.txt";
            sheet.Cell(2, 2).Value = "https://drive.google.com/file/d/File_2/view?resourcekey=Key_2";
            // Повністю порожній фізичний рядок 3 не має входити до статистики.
            sheet.Cell(4, 1).Value = "Недоступний";
            sheet.Cell(4, 2).Value = "https://drive.google.com/open?id=File_4";
            sheet.Cell(5, 1).Value = "Неправильний URL";
            sheet.Cell(5, 2).Value = "https://example.org/private";
            sheet.Cell(6, 1).Value = "Експорт.pdf";
            sheet.Cell(6, 2).Value = "Google Doc";
            sheet.Cell(6, 2).SetHyperlink(new XLHyperlink("https://docs.google.com/document/d/File_6/edit"));
            workbook.SaveAs(input);
        }
        var before = await File.ReadAllBytesAsync(input);
        var output = Directory.CreateDirectory(Path.Combine(directory.Path, "output")).FullName;
        await File.WriteAllTextAsync(Path.Combine(output, "Звіт.pdf"), "existing");
        var mediaAttempts = 0;
        using var transport = new DriveTestTransport
        {
            RespondAsync = (request, _) =>
            {
                var uri = request.RequestUri!;
                HttpResponseMessage response;
                if (uri.AbsolutePath.EndsWith("/File_4", StringComparison.Ordinal))
                    response = DriveTestTransport.Error(HttpStatusCode.NotFound);
                else if (uri.AbsolutePath.EndsWith("/export", StringComparison.Ordinal))
                    response = DriveTestTransport.Content([5, 6, 7]);
                else if (uri.Query.Contains("alt=media", StringComparison.Ordinal))
                {
                    mediaAttempts++;
                    response = mediaAttempts switch
                    {
                        1 => DriveTestTransport.Error(HttpStatusCode.Forbidden, "userRateLimitExceeded", retryAfter: 7),
                        2 => DriveTestTransport.Content([9, 9]), // SDK завершить читання, але перевірка розміру відхилить файл.
                        _ => DriveTestTransport.Content([1, 2, 3, 4])
                    };
                }
                else if (uri.AbsolutePath.EndsWith("/File_6", StringComparison.Ordinal))
                    response = DriveTestTransport.Metadata("File_6", "application/vnd.google-apps.document", null, null, name: "source");
                else response = DriveTestTransport.Metadata("File_2");
                return Task.FromResult(response);
            }
        };
        using var drive = GoogleDriveServiceTests.Create(transport);
        var log = new StorageTestLog();
        var retry = new BatchTestRetry();
        var progress = new BatchTestProgress();
        var service = new BatchDownloadService(new ExcelReader(new DriveLinkParser()), drive, new FileNameResolver(),
            new FileStorageService(new FileNameResolver(), log), retry, log);

        var result = await service.RunAsync(input, output, progress);

        Assert.Equal(BatchCompletionStatus.Completed, result.Status);
        Assert.Equal((4, 4, 2, 2, 0), (result.Total, result.Processed, result.Succeeded, result.Failed, result.Skipped));
        Assert.Equal(new[] { 2, 4, 5, 6 }, result.Results.Select(row => row.RowNumber));
        Assert.Equal(new[] { 3, 1, 0, 1 }, result.Results.Select(row => row.Attempts));
        Assert.Equal(new[] { TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(2.25) }, retry.Delays);
        Assert.Equal(3, mediaAttempts); // Внутрішні повтори SDK не множать бюджет пакета.
        Assert.Equal(3, transport.Requests.Count(request => request.Uri.AbsolutePath.EndsWith("/File_2", StringComparison.Ordinal)
            && !request.Uri.Query.Contains("alt=media", StringComparison.Ordinal)));
        Assert.All(transport.Requests.Where(request => request.Uri.AbsolutePath.EndsWith("/File_2", StringComparison.Ordinal)),
            request => Assert.Equal("File_2/Key_2", request.ResourceKeys));
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(output, "Звіт.pdf")));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(Path.Combine(output, "Звіт (2).pdf")));
        Assert.Equal(new byte[] { 5, 6, 7 }, await File.ReadAllBytesAsync(Path.Combine(output, "Експорт.docx")));
        Assert.Equal(3, Directory.GetFiles(output).Length);
        Assert.Empty(Directory.GetFiles(output, "*.part"));
        Assert.Equal(before, await File.ReadAllBytesAsync(input));
        Assert.Equal(4, progress.Values[^1].Processed);
        Assert.All(log.Entries, entry =>
        {
            Assert.DoesNotContain("DO-NOT-EXPOSE", entry.Message);
            Assert.DoesNotContain("Key_2", entry.ToString());
            Assert.Null(entry.ExceptionDetails);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkbookReadFailureOrPreCancellationHasDistinctTerminalStatus(bool cancel)
    {
        using var context = new BatchTestContext();
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.Cancel();
        var result = await context.Service.RunAsync(Path.Combine(context.Directory, "missing.xlsx"), context.Directory,
            context.Progress, cancellation.Token);
        Assert.Equal(cancel ? BatchCompletionStatus.Cancelled : BatchCompletionStatus.Failed, result.Status);
        Assert.Equal(0, result.Total);
        Assert.Equal(0, context.Drive.MetadataCalls);
        Assert.Equal(result.Status, context.Progress.Values[^1].CompletionStatus);
        Assert.False(context.Service.IsRunning);
        Assert.Equal(BatchCompletionStatus.Completed, (await context.RunAsync(BatchTestContext.Rows(0))).Status);
    }
}
