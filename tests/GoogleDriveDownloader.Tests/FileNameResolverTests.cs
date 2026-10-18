using GoogleDriveDownloader.Application.Configuration;
using GoogleDriveDownloader.Application.Drive;
using GoogleDriveDownloader.Application.Storage;

namespace GoogleDriveDownloader.Tests;

public sealed class FileNameResolverTests
{
    private readonly FileNameResolver _resolver = new();
    private static DriveFileMetadata Metadata(string mime = "application/pdf", string? extension = "pdf", string name = "source.pdf") =>
        new("File_1", name, mime, extension, 10, false, true);

    [Theory]
    [InlineData("Звіт за 2025 рік", "Звіт за 2025 рік.pdf")]
    [InlineData("Звіт.pdf", "Звіт.pdf")]
    [InlineData("Звіт.PDF", "Звіт.pdf")]
    [InlineData("Звіт.pdf.pdf", "Звіт.pdf")]
    [InlineData("Звіт.docx", "Звіт.pdf")]
    [InlineData("Звіт.txt", "Звіт.pdf")]
    [InlineData("Звіт.v2.final", "Звіт.v2.final.pdf")]
    [InlineData("Звіт.2025", "Звіт.2025.pdf")]
    [InlineData("Звіт.2025.xlsx", "Звіт.2025.pdf")]
    [InlineData("Звіт.final.pdf", "Звіт.final.pdf")]
    [InlineData("  Звіт.pdf.  ", "Звіт.pdf")]
    [InlineData("", "Документ.pdf")]
    [InlineData(null, "Документ.pdf")]
    [InlineData(" ... ", "Документ.pdf")]
    [InlineData("<Звіт>:A/B\\C|D?E*F\"", "_Звіт__A_B_C_D_E_F_.pdf")]
    [InlineData("Звіт\0\t\n", "Звіт___.pdf")]
    [InlineData("CON", "_CON.pdf")]
    [InlineData("PRN", "_PRN.pdf")]
    [InlineData("AUX", "_AUX.pdf")]
    [InlineData("LPT¹", "_LPT¹.pdf")]
    [InlineData("con.txt", "_con.pdf")]
    [InlineData("NUL.final", "_NUL.final.pdf")]
    [InlineData("CON .final", "_CON .final.pdf")]
    [InlineData("COM1", "_COM1.pdf")]
    [InlineData("lpt9", "_lpt9.pdf")]
    [InlineData("COM¹", "_COM¹.pdf")]
    [InlineData("LPT²", "_LPT².pdf")]
    [InlineData("COM³", "_COM³.pdf")]
    [InlineData("CONIN$", "_CONIN$.pdf")]
    [InlineData("COM10", "COM10.pdf")]
    [InlineData("CONTEXT", "CONTEXT.pdf")]
    public void ProducesSafeNamesWithoutLosingMeaningfulDots(string? requested, string expected)
    {
        var result = _resolver.Resolve(requested, Metadata(), new DriveTransferFormat("application/pdf"));
        Assert.Equal(expected, result);
        FileNameResolver.ValidateFileName(result);
    }

    [Theory]
    [InlineData(ExportSettings.Docx, ".docx", "Договір.pdf", "Договір.docx")]
    [InlineData(ExportSettings.Xlsx, ".xlsx", "Звіт.xls", "Звіт.xlsx")]
    [InlineData(ExportSettings.Pptx, ".pptx", "Презентація.2025.pptx", "Презентація.2025.pptx")]
    public void ExportFormatOverridesRemoteMetadata(string mime, string extension, string requested, string expected)
    {
        Assert.Equal(expected, _resolver.Resolve(requested, Metadata("application/vnd.google-apps.document", "bad"), new DriveTransferFormat(mime, extension)));
    }

    [Theory]
    [InlineData("application/pdf", "docx", "wrong.docx", "report.pdf")]
    [InlineData("image/jpeg", "jpeg", "photo.jpeg", "report.jpeg")]
    [InlineData("text/plain", "log", "source.log", "report.log")]
    [InlineData("text/plain", null, "source", "report.txt")]
    [InlineData("application/octet-stream", "custom", "source.custom", "report.custom")]
    [InlineData("application/octet-stream", "ДАНІ", "source.дані", "report.дані")]
    [InlineData("application/octet-stream", null, "source.custom", "report.custom")]
    [InlineData("application/octet-stream", null, "source", "report")]
    [InlineData("application/pdf", "../outside", "source", "report.pdf")]
    public void DeterminesExtensionFromActualMetadata(string mime, string? extension, string remoteName, string expected)
    {
        Assert.Equal(expected, _resolver.Resolve("report", Metadata(mime, extension, remoteName), new DriveTransferFormat(mime)));
    }

    [Fact]
    public void ExistingCompoundNameIsPreservedWhenFinalExtensionIsCorrect()
    {
        Assert.Equal("backup.tar.gz", _resolver.Resolve("backup.tar.gz", Metadata("application/gzip", "gz", "source.tar.gz"), new DriveTransferFormat("application/gzip")));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..\\..\\outside.pdf")]
    [InlineData("C:\\Users\\Private\\outside.txt")]
    [InlineData("\\\\server\\share\\outside")]
    [InlineData("\\\\?\\C:\\outside")]
    [InlineData("safe.pdf:alternate-stream")]
    public void UntrustedInputBecomesOneFileName(string requested)
    {
        var result = _resolver.Resolve(requested, Metadata(), new DriveTransferFormat("application/pdf"));
        Assert.Equal(result, Path.GetFileName(result));
        Assert.False(Path.IsPathRooted(result));
        Assert.DoesNotContain("/", result);
        Assert.DoesNotContain("\\", result);
        Assert.DoesNotContain(":", result);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../outside.pdf")]
    [InlineData("C:\\outside.pdf")]
    [InlineData("name.pdf:stream")]
    [InlineData("NUL.txt")]
    [InlineData("COM².pdf")]
    [InlineData("name. ")]
    [InlineData("name\0.pdf")]
    public void StorageBoundaryRejectsUnresolvedUnsafeNames(string fileName)
    {
        Assert.Equal(FileStorageErrorCode.InvalidName, Assert.Throws<FileStorageException>(() => FileNameResolver.ValidateFileName(fileName)).Code);
    }

    [Fact]
    public void LongNamesAndCollisionSuffixesRespectBudgetAndSurrogatePairs()
    {
        var requested = string.Concat(Enumerable.Repeat("😀", 200));
        var name = _resolver.Resolve(requested, Metadata(), new DriveTransferFormat("application/pdf"));
        Assert.InRange(name.Length, 1, 255);
        Assert.EndsWith(".pdf", name);
        var candidate = _resolver.GetCandidate(name, 123, maxLength: 100);
        Assert.InRange(candidate.Length, 1, 100);
        Assert.EndsWith(" (123).pdf", candidate);
        FileNameResolver.ValidateFileName(candidate);
        Assert.DoesNotContain('\ufffd', candidate);
    }

    [Fact]
    public void TruncationCannotProduceReservedName()
    {
        Assert.Equal("_CO.pdf", _resolver.Resolve("CONTRACT", Metadata(), new DriveTransferFormat("application/pdf"), maxLength: 7));
    }

    [Fact]
    public void CannotFitExtensionAndSuffixHasExplicitError()
    {
        Assert.Equal(FileStorageErrorCode.PathTooLong, Assert.Throws<FileStorageException>(() => _resolver.GetCandidate("report.pdf", 2, 5)).Code);
    }

    [Fact]
    public void InvalidExportCannotSmugglePathIntoExtension()
    {
        Assert.Throws<FileStorageException>(() => _resolver.Resolve("report", Metadata(), new DriveTransferFormat(ExportSettings.Docx, "../outside")));
    }
}
