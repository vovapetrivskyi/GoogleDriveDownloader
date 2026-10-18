using GoogleDriveDownloader.Application.Input;

namespace GoogleDriveDownloader.Tests;

public sealed class SampleWorkbookTests
{
    internal static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "Samples", name);

    [Fact]
    public async Task DownloadSampleHasFiveSyntheticRecordsWithoutModifyingWorkbook()
    {
        var path = Sample("downloads.xlsx");
        var before = await File.ReadAllBytesAsync(path);
        var result = await new ExcelReader(new DriveLinkParser()).ReadAsync(path);
        Assert.Equal(5, result.Rows.Count);
        Assert.All(result.Rows, row => { Assert.True(row.IsValid); Assert.StartsWith("SYNTHETIC_", row.Link!.FileId); });
        Assert.Equal("SYNTHETIC_KEY_001", result.Rows[1].Link!.ResourceKey);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ValidationSampleRetainsPhysicalRowsHyperlinkAndStoredFormulaWithoutRecalculation()
    {
        var path = Sample("validation.xlsx");
        var before = await File.ReadAllBytesAsync(path);
        var result = await new ExcelReader(new DriveLinkParser()).ReadAsync(path);
        Assert.Equal(new[] { 2, 4, 5, 6, 7, 8 }, result.Rows.Select(row => row.RowNumber));
        Assert.Equal(new[] { true, false, false, false, true, false }, result.Rows.Select(row => row.IsValid));
        Assert.Equal("SYNTHETIC_KEY_002", result.Rows[0].Link!.ResourceKey);
        Assert.Equal("../../CON", result.Rows[0].DocumentName);
        Assert.Equal("Кешована назва", result.Rows[4].DocumentName);
        Assert.All(result.Rows.Where(row => !row.IsValid), row => Assert.NotEmpty(row.Errors));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task InvalidHeaderSampleHasWorkbookErrorAndCanStillBeOpenedExclusively()
    {
        var path = Sample("invalid-headers.xlsx");
        await Assert.ThrowsAsync<ExcelReadException>(() => new ExcelReader(new DriveLinkParser()).ReadAsync(path));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
    }
}
