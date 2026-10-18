using System.Globalization;
using ClosedXML.Excel;

namespace GoogleDriveDownloader.Application.Input;

public sealed class ExcelReader(DriveLinkParser linkParser)
{
    public Task<ExcelReadResult> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => ReadCore(path, cancellationToken), cancellationToken);

    private ExcelReadResult ReadCore(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path) || !Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new ExcelReadException("Оберіть файл Excel у форматі .xlsx.");

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            // ClosedXML має синхронний loader: він виконується у Task.Run, без перерахунку формул.
            using var workbook = new XLWorkbook(stream, new LoadOptions { RecalculateAllFormulas = false });
            cancellationToken.ThrowIfCancellationRequested();
            if (workbook.Worksheets.Count == 0)
                throw new ExcelReadException("Книга не містить аркушів.");

            var worksheet = workbook.Worksheet(1);
            var rowNumbers = new SortedSet<int>();
            foreach (var row in worksheet.RowsUsed(XLCellsUsedOptions.Contents))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (row.CellsUsed(XLCellsUsedOptions.Contents).Any(HasContent)) rowNumbers.Add(row.RowNumber());
            }
            // Гіперпосилання без тексту не входить до CellsUsed(Contents), але є непорожнім записом.
            foreach (var hyperlink in worksheet.Hyperlinks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (hyperlink.Cell is { } cell) rowNumbers.Add(cell.Address.RowNumber);
            }
            if (rowNumbers.Count == 0)
                throw new ExcelReadException("Перший аркуш порожній. Додайте заголовки «Назва» та «Посилання».");

            var headerRow = rowNumbers.Min;
            var nameColumn = 0;
            var linkColumn = 0;
            foreach (var cell in worksheet.Row(headerRow).CellsUsed(XLCellsUsedOptions.Contents))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = cell.CachedValue;
                if (!value.IsText) continue;
                var header = value.GetText().Trim();
                if (header.Equals("Назва", StringComparison.OrdinalIgnoreCase))
                {
                    if (nameColumn != 0) throw new ExcelReadException($"Рядок {headerRow}: заголовок «Назва» дублюється.");
                    nameColumn = cell.Address.ColumnNumber;
                }
                if (header.Equals("Посилання", StringComparison.OrdinalIgnoreCase))
                {
                    if (linkColumn != 0) throw new ExcelReadException($"Рядок {headerRow}: заголовок «Посилання» дублюється.");
                    linkColumn = cell.Address.ColumnNumber;
                }
            }
            if (nameColumn == 0 || linkColumn == 0)
                throw new ExcelReadException($"Рядок {headerRow}: потрібні унікальні заголовки «Назва» та «Посилання».");

            var rows = new List<InputRow>();
            foreach (var rowNumber in rowNumbers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rowNumber == headerRow) continue;
                var errors = new List<string>();
                var name = ReadText(worksheet.Cell(rowNumber, nameColumn), "Назва", errors);
                var linkCell = worksheet.Cell(rowNumber, linkColumn);
                string? url;
                if (linkCell.HasHyperlink)
                {
                    var hyperlink = linkCell.GetHyperlink();
                    url = hyperlink.IsExternal ? hyperlink.ExternalAddress?.OriginalString : null;
                    if (url is null) errors.Add("«Посилання»: потрібне зовнішнє HTTPS-гіперпосилання.");
                }
                else
                {
                    var errorCount = errors.Count;
                    url = ReadText(linkCell, "Посилання", errors);
                    if (errors.Count != errorCount) url = null;
                }

                DriveLink? link = null;
                if (url is not null)
                {
                    try { link = linkParser.Parse(url); }
                    catch (DriveLinkException exception) { errors.Add(exception.Message); }
                }
                if (name.Length == 0 && !errors.Any(error => error.StartsWith("«Назва»", StringComparison.Ordinal)))
                    errors.Add("«Назва»: значення порожнє.");
                var rowErrors = errors.Select(error => $"Рядок {rowNumber}: {error}").ToList().AsReadOnly();
                rows.Add(new InputRow(rowNumber, name, link, rowErrors));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new ExcelReadResult(headerRow, rows.AsReadOnly());
        }
        catch (OperationCanceledException) { throw; }
        catch (ExcelReadException) { throw; }
        catch (Exception)
        {
            // Помилки OpenXML/ZIP можуть містити шлях чи фрагменти вхідних даних; не передаємо їх до UI або журналу.
            cancellationToken.ThrowIfCancellationRequested();
            throw new ExcelReadException("Не вдалося прочитати XLSX. Перевірте формат і цілісність книги, наявність файла та права доступу.");
        }
    }

    private static bool HasContent(IXLCell cell) => cell.HasFormula || cell.HasHyperlink ||
        (!cell.CachedValue.IsBlank && (!cell.CachedValue.IsText || !string.IsNullOrWhiteSpace(cell.CachedValue.GetText())));

    private static string ReadText(IXLCell cell, string column, List<string> errors)
    {
        // Value, GetString і GetFormattedString можуть запускати обчислення. Використовуємо лише CachedValue.
        var value = cell.CachedValue;
        if (value.IsError || (cell.HasFormula && (value.IsBlank || (value.IsText && string.IsNullOrWhiteSpace(value.GetText())))))
        {
            errors.Add(cell.HasFormula
                ? $"«{column}»: формула не має придатного збереженого результату. Збережіть обчислене значення в Excel або замініть формулу текстом."
                : $"«{column}»: комірка містить помилку Excel.");
            return "";
        }
        return value.IsBlank ? "" : value.ToString(CultureInfo.InvariantCulture).Trim();
    }
}
