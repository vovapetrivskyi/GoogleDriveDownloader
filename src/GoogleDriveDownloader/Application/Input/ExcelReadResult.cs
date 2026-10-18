namespace GoogleDriveDownloader.Application.Input;

public sealed record InputRow(int RowNumber, string DocumentName, DriveLink? Link, IReadOnlyList<string> Errors)
{
    public bool IsValid => Link is not null && Errors.Count == 0;
}

public sealed record ExcelReadResult(int HeaderRowNumber, IReadOnlyList<InputRow> Rows);
