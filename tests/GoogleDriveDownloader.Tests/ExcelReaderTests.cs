using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using ClosedXML.Excel;
using GoogleDriveDownloader.Application.Input;

namespace GoogleDriveDownloader.Tests;

public sealed class ExcelReaderTests
{
    private const string Url = "https://drive.google.com/file/d/FILE_ID_1/view";
    private readonly ExcelReader _reader = new(new DriveLinkParser());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsHeadersInEitherOrderIgnoringCaseAndWhitespace(bool reversed)
    {
        using var directory = new TestDirectory();
        var path = Save(directory, sheet =>
        {
            sheet.Cell(1, reversed ? 2 : 1).Value = "  нАЗВА  ";
            sheet.Cell(1, reversed ? 1 : 2).Value = " ПоСиЛаНнЯ ";
            sheet.Cell(2, reversed ? 2 : 1).Value = "  Звіт за 2025 рік  ";
            sheet.Cell(2, reversed ? 1 : 2).Value = Url;
        });
        var result = await _reader.ReadAsync(path);
        Assert.Equal(1, result.HeaderRowNumber);
        var row = Assert.Single(result.Rows);
        Assert.True(row.IsValid);
        Assert.Equal(2, row.RowNumber);
        Assert.Equal("Звіт за 2025 рік", row.DocumentName);
        Assert.Equal(new DriveLink("FILE_ID_1"), row.Link);
        Assert.Empty(row.Errors);
    }

    [Fact]
    public async Task PreservesPhysicalRowsAndIgnoresEmptyWhitespaceAndFormattingOnlyRows()
    {
        using var directory = new TestDirectory();
        var path = Save(directory, sheet =>
        {
            sheet.Cell("A1").Value = " \t ";
            sheet.Cell("A2").Style.Fill.BackgroundColor = XLColor.Yellow;
            sheet.Cell("D4").Value = "Назва";
            sheet.Cell("H4").Value = "Посилання";
            sheet.Cell("D7").Value = "Перший";
            sheet.Cell("H7").Value = Url;
            sheet.Cell("D8").Value = " ";
            sheet.Cell("H8").Value = "\t";
            sheet.Cell("D12").Value = "Другий";
            sheet.Cell("H12").Value = "https://docs.google.com/document/d/FILE_ID_2/edit";
            sheet.Cell("A200").Style.Font.Bold = true;
        });
        var result = await _reader.ReadAsync(path);
        Assert.Equal(4, result.HeaderRowNumber);
        Assert.Equal([7, 12], result.Rows.Select(row => row.RowNumber));
        Assert.All(result.Rows, row => Assert.True(row.IsValid));
    }

    [Fact]
    public async Task OnlyReadsFirstWorksheetEvenWhenAnotherSheetIsActive()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "book.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var first = workbook.AddWorksheet("Перший");
            Headers(first);
            first.Cell("A2").Value = "Перший файл";
            first.Cell("B2").Value = Url;
            var second = workbook.AddWorksheet("Другий");
            second.Cell("A1").Value = "Неправильні заголовки";
            second.SetTabActive();
            workbook.SaveAs(path);
        }
        Assert.Equal("Перший файл", Assert.Single((await _reader.ReadAsync(path)).Rows).DocumentName);
    }

    [Fact]
    public async Task HyperlinkTargetHasPriorityOverDisplayedText()
    {
        using var directory = new TestDirectory();
        var path = Save(directory, sheet =>
        {
            Headers(sheet);
            sheet.Cell("A2").Value = "Документ";
            sheet.Cell("B2").Value = "https://drive.google.com/file/d/WRONG_ID/view";
            sheet.Cell("B2").SetHyperlink(new XLHyperlink(Url + "?resourcekey=0-Key_123"));
        });
        var row = Assert.Single((await _reader.ReadAsync(path)).Rows);
        Assert.True(row.IsValid);
        Assert.Equal(new DriveLink("FILE_ID_1", "0-Key_123"), row.Link);
    }

    [Fact]
    public async Task HyperlinkWithoutTextIsNotIgnoredAndCanSupplyUrl()
    {
        using var directory = new TestDirectory();
        var path = Save(directory, sheet =>
        {
            Headers(sheet);
            sheet.Cell("A3").Value = "Без тексту посилання";
            sheet.Cell("B3").SetHyperlink(new XLHyperlink(Url));
            sheet.Cell("B8").SetHyperlink(new XLHyperlink(Url));
        });
        var rows = (await _reader.ReadAsync(path)).Rows;
        Assert.Equal([3, 8], rows.Select(row => row.RowNumber));
        Assert.True(rows[0].IsValid);
        Assert.False(rows[1].IsValid);
        Assert.Contains(rows[1].Errors, error => error.Contains("«Назва»"));
    }

    [Theory]
    [InlineData("https://example.com/invalid")]
    [InlineData("#Лист1!A1")]
    public async Task InvalidHyperlinkDoesNotFallBackToValidDisplayedUrl(string target)
    {
        using var directory = new TestDirectory();
        var path = Save(directory, sheet =>
        {
            Headers(sheet);
            sheet.Cell("A2").Value = "Документ";
            sheet.Cell("B2").Value = Url;
            sheet.Cell("B2").SetHyperlink(new XLHyperlink(target));
        });
        var row = Assert.Single((await _reader.ReadAsync(path)).Rows);
        Assert.False(row.IsValid);
        Assert.Null(row.Link);
        Assert.All(row.Errors, error => Assert.StartsWith("Рядок 2:", error));
    }

    [Fact]
    public async Task KeepsIndependentErrorsAndContinuesAfterInvalidRows()
    {
        using var directory = new TestDirectory();
        var path = Save(directory, sheet =>
        {
            Headers(sheet);
            sheet.Cell("B2").Value = "https://evil.test/file/d/FAKE/view";
            sheet.Cell("A3").Value = "Без посилання";
            sheet.Cell("A4").Value = "З правильним посиланням";
            sheet.Cell("B4").Value = Url;
            sheet.Cell("C5").Value = "Лише зайвий стовпець";
        });
        var rows = (await _reader.ReadAsync(path)).Rows;
        Assert.Equal(4, rows.Count);
        Assert.Equal(2, rows[0].Errors.Count);
        Assert.False(rows[1].IsValid);
        Assert.True(rows[2].IsValid);
        Assert.False(rows[3].IsValid);
        Assert.All(rows, row => Assert.All(row.Errors, error => Assert.StartsWith($"Рядок {row.RowNumber}:", error)));
    }

    [Theory]
    [InlineData("Назва", "Неправильне", "")]
    [InlineData("Неправильне", "Посилання", "")]
    [InlineData("Назва", "Посилання", " НАЗВА ")]
    [InlineData("Назва", "Посилання", "поСИЛАння")]
    public async Task MissingOrDuplicateRequiredHeadersAreWorkbookErrors(string first, string second, string third)
    {
        using var directory = new TestDirectory();
        var path = Save(directory, sheet =>
        {
            sheet.Cell("A3").Value = first;
            sheet.Cell("B3").Value = second;
            sheet.Cell("C3").Value = third;
            sheet.Cell("A4").Value = "Документ";
            sheet.Cell("B4").Value = Url;
        });
        var exception = await Assert.ThrowsAsync<ExcelReadException>(() => _reader.ReadAsync(path));
        Assert.Contains("Рядок 3", exception.Message);
    }

    [Fact]
    public async Task DoesNotSearchPastFirstNonemptyRowForHeaders()
    {
        using var directory = new TestDirectory();
        var path = Save(directory, sheet =>
        {
            sheet.Cell("A1").Value = "Преамбула";
            sheet.Cell("A2").Value = "Назва";
            sheet.Cell("B2").Value = "Посилання";
        });
        await Assert.ThrowsAsync<ExcelReadException>(() => _reader.ReadAsync(path));
    }

    [Fact]
    public async Task HeadersWithoutDataProduceEmptyResult()
    {
        using var directory = new TestDirectory();
        var result = await _reader.ReadAsync(Save(directory, Headers));
        Assert.Equal(1, result.HeaderRowNumber);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task EmptyFirstWorksheetDoesNotFallBackToSecondWorksheet()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "book.xlsx");
        using (var workbook = new XLWorkbook())
        {
            workbook.AddWorksheet("Порожній");
            Headers(workbook.AddWorksheet("Із заголовками"));
            workbook.SaveAs(path);
        }
        await Assert.ThrowsAsync<ExcelReadException>(() => _reader.ReadAsync(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyOrOnlyFormattedWorkbookHasClearError(bool formatted)
    {
        using var directory = new TestDirectory();
        var path = Save(directory, sheet =>
        {
            if (formatted) sheet.Cell("Z500").Style.Font.Bold = true;
        });
        var exception = await Assert.ThrowsAsync<ExcelReadException>(() => _reader.ReadAsync(path));
        Assert.Contains("порожній", exception.Message);
    }

    [Theory]
    [InlineData("1+1")]
    [InlineData("HYPERLINK(\"https://drive.google.com/file/d/FILE_ID_1/view\",\"Відкрити\")")]
    [InlineData("A2")]
    [InlineData("UNSUPPORTED_FUNCTION()")]
    public async Task FormulaWithoutUsableCacheIsNotEvaluatedAndBecomesRowError(string formula)
    {
        using var directory = new TestDirectory();
        var path = Save(directory, sheet =>
        {
            Headers(sheet);
            sheet.Cell("A2").Value = "Документ";
            sheet.Cell("B2").FormulaA1 = formula;
            sheet.Cell("A3").Value = "Наступний";
            sheet.Cell("B3").Value = Url;
        });
        var before = await File.ReadAllBytesAsync(path);
        var result = await _reader.ReadAsync(path);
        Assert.False(result.Rows[0].IsValid);
        Assert.Contains(result.Rows[0].Errors, error => error.Contains("формула"));
        Assert.True(result.Rows[1].IsValid);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task UsesStoredFormulaValuesEvenWhenFormulaWouldReturnDifferentResult()
    {
        using var directory = new TestDirectory();
        var path = Save(directory, sheet =>
        {
            Headers(sheet);
            sheet.Cell("A2").Value = "Placeholder";
            sheet.Cell("B2").Value = "Placeholder";
        });
        SetFormulaCache(path, "A2", "UNSUPPORTED_FUNCTION()", "Збережена назва");
        SetFormulaCache(path, "B2", "\"https://evil.test\"", Url);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(path));
        var row = Assert.Single((await _reader.ReadAsync(path)).Rows);
        Assert.True(row.IsValid);
        Assert.Equal("Збережена назва", row.DocumentName);
        Assert.Equal("FILE_ID_1", row.Link!.FileId);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(path)));
    }

    [Fact]
    public async Task FormulaOnlyRowWithoutCacheIsNotConsideredBlank()
    {
        using var directory = new TestDirectory();
        var path = Save(directory, sheet =>
        {
            Headers(sheet);
            sheet.Cell("A4").FormulaA1 = "\"\"";
            sheet.Cell("B4").FormulaA1 = "\"\"";
        });
        var row = Assert.Single((await _reader.ReadAsync(path)).Rows);
        Assert.Equal(4, row.RowNumber);
        Assert.False(row.IsValid);
    }

    [Fact]
    public async Task RealHyperlinkTakesPriorityEvenWhenCellContainsUncachedFormula()
    {
        using var directory = new TestDirectory();
        var path = Save(directory, sheet =>
        {
            Headers(sheet);
            sheet.Cell("A2").Value = "Документ";
            sheet.Cell("B2").FormulaA1 = "UNSUPPORTED_FUNCTION()";
            sheet.Cell("B2").SetHyperlink(new XLHyperlink(Url));
        });
        Assert.True(Assert.Single((await _reader.ReadAsync(path)).Rows).IsValid);
    }

    [Fact]
    public async Task ExcelErrorCellsAreIndependentValidationErrors()
    {
        using var directory = new TestDirectory();
        var path = Save(directory, sheet =>
        {
            Headers(sheet);
            sheet.Cell("A2").Value = XLError.DivisionByZero;
            sheet.Cell("B2").Value = XLError.CellReference;
            sheet.Cell("A3").Value = "Документ";
            sheet.Cell("B3").Value = Url;
        });
        var result = await _reader.ReadAsync(path);
        Assert.False(result.Rows[0].IsValid);
        Assert.True(result.Rows[1].IsValid);
        Assert.Contains(result.Rows[0].Errors, error => error.Contains("помилку Excel"));
    }

    [Fact]
    public async Task CanReadReadonlyFileRepeatedlyWithoutChangingBytesOrTimestampAndReleasesHandles()
    {
        using var directory = new TestDirectory();
        var path = Save(directory, sheet =>
        {
            Headers(sheet);
            sheet.Cell("A2").Value = 2025;
            sheet.Cell("B2").Value = Url;
        });
        var bytes = await File.ReadAllBytesAsync(path);
        var modified = File.GetLastWriteTimeUtc(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var results = await Task.WhenAll(_reader.ReadAsync(path), _reader.ReadAsync(path));
            Assert.All(results, result => Assert.Equal("2025", Assert.Single(result.Rows).DocumentName));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing.xlsx")]
    [InlineData("book.xls")]
    public async Task InvalidPathOrFormatHasSafeError(string fileName)
    {
        using var directory = new TestDirectory();
        await Assert.ThrowsAsync<ExcelReadException>(() => _reader.ReadAsync(Path.Combine(directory.Path, fileName)));
    }

    [Fact]
    public async Task DamagedWorkbookAndLockedFileHaveSafeErrorsAndReleaseResources()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "private-synthetic-input.xlsx");
        await File.WriteAllTextAsync(path, "synthetic-private-file-contents");
        var exception = await Assert.ThrowsAsync<ExcelReadException>(() => _reader.ReadAsync(path));
        Assert.DoesNotContain("synthetic-", exception.ToString());
        Assert.Null(exception.InnerException);
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<ExcelReadException>(() => _reader.ReadAsync(path));
    }

    [Fact]
    public async Task PreCancelledReadDoesNotOpenOrValidateFile()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _reader.ReadAsync("missing.xlsx", cancellation.Token));
    }

    [Fact]
    public async Task BrokenOpenXmlPackageIsRejectedWithoutLeakingFileContents()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "broken.xlsx");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("[Content_Types].xml").Open());
            writer.Write("<synthetic-private-invalid-xml");
        }
        var exception = await Assert.ThrowsAsync<ExcelReadException>(() => _reader.ReadAsync(path));
        Assert.DoesNotContain("synthetic-private", exception.ToString());
        Assert.Null(exception.InnerException);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static string Save(TestDirectory directory, Action<IXLWorksheet> configure)
    {
        var path = Path.Combine(directory.Path, "book.xlsx");
        using var workbook = new XLWorkbook();
        configure(workbook.AddWorksheet("Лист1"));
        workbook.SaveAs(path, new ClosedXML.Excel.SaveOptions { EvaluateFormulasBeforeSaving = false });
        return path;
    }

    private static void Headers(IXLWorksheet sheet)
    {
        sheet.Cell("A1").Value = "Назва";
        sheet.Cell("B1").Value = "Посилання";
    }

    private static void SetFormulaCache(string path, string address, string formula, string cached)
    {
        // Записуємо OOXML напряму: тест не повинен обчислювати формулу, щоб підготувати кеш.
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = archive.GetEntry("xl/worksheets/sheet1.xml")!;
        XDocument document;
        using (var stream = entry.Open()) document = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var cell = document.Descendants(ns + "c").Single(element => (string?)element.Attribute("r") == address);
        cell.SetAttributeValue("t", "str");
        cell.RemoveNodes();
        cell.Add(new XElement(ns + "f", formula), new XElement(ns + "v", cached));
        entry.Delete();
        using var output = archive.CreateEntry("xl/worksheets/sheet1.xml").Open();
        document.Save(output);
    }
}
