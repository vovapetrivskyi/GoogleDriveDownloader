using GoogleDriveDownloader.Application.Input;

namespace GoogleDriveDownloader.Tests;

public sealed class DriveLinkParserTests
{
    private readonly DriveLinkParser _parser = new();

    [Theory]
    [InlineData("https://drive.google.com/file/d/FILE_ID_1/view", "FILE_ID_1")]
    [InlineData("https://drive.google.com/open?id=FILE_ID_2", "FILE_ID_2")]
    [InlineData("https://drive.google.com/uc?id=FILE_ID_3", "FILE_ID_3")]
    [InlineData("https://docs.google.com/document/d/FILE_ID_4/edit", "FILE_ID_4")]
    [InlineData("https://docs.google.com/spreadsheets/d/FILE_ID_5/edit", "FILE_ID_5")]
    [InlineData("https://docs.google.com/presentation/d/FILE_ID_6/edit", "FILE_ID_6")]
    [InlineData("  HTTPS://DRIVE.GOOGLE.COM:443/file/d/AbC_123-xy/view?usp=sharing  ", "AbC_123-xy")]
    [InlineData("https://drive.google.com/file/d/id_1", "id_1")]
    [InlineData("https://drive.google.com/file/d/id_1/preview/", "id_1")]
    [InlineData("https://docs.google.com/document/d/id_1/copy", "id_1")]
    [InlineData("https://docs.google.com/spreadsheets/d/id_1/edit#gid=0", "id_1")]
    [InlineData("https://docs.google.com/presentation/d/id_1/view", "id_1")]
    [InlineData("https://drive.google.com/uc?export=download&id=AbC_123-xy", "AbC_123-xy")]
    [InlineData("https://drive.google.com/open/?usp=sharing&ID=AbC_123-xy", "AbC_123-xy")]
    [InlineData("https://drive.google.com/file/d/id%5F1/view?id=id_1", "id_1")]
    public void ParsesRequiredAndSupportedAlternateLinks(string url, string expectedId)
    {
        var result = _parser.Parse(url);
        Assert.Equal(expectedId, result.FileId);
        Assert.Null(result.ResourceKey);
    }

    [Theory]
    [InlineData("https://drive.google.com/open?resourcekey=0-Key_123&id=FileId", "0-Key_123")]
    [InlineData("https://drive.google.com/uc?id=FileId&resourcekey=0-Key_123&export=download", "0-Key_123")]
    [InlineData("https://drive.google.com/file/d/FileId/view?resourcekey=0-Key_123", "0-Key_123")]
    [InlineData("https://docs.google.com/document/d/FileId/edit?resourcekey=0-Key_123", "0-Key_123")]
    [InlineData("https://docs.google.com/spreadsheets/d/FileId/edit?resourcekey=0-Key_123#gid=0", "0-Key_123")]
    [InlineData("https://docs.google.com/presentation/d/FileId/edit?resourcekey=0-Key_123", "0-Key_123")]
    [InlineData("https://drive.google.com/open?%69d=FileId&resource%6Bey=0%2DKey%5F123", "0-Key_123")]
    [InlineData("https://drive.google.com/open?id=FileId&RESOURCEKEY=a%2Bb%3D", "a+b=")]
    public void PreservesResourceKeySeparatelyFromFileId(string url, string expectedKey)
    {
        Assert.Equal(new DriveLink("FileId", expectedKey), _parser.Parse(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("FILE_ID")]
    [InlineData("not a url")]
    [InlineData("/file/d/id/view")]
    [InlineData("http://drive.google.com/file/d/id/view")]
    [InlineData("ftp://drive.google.com/file/d/id/view")]
    [InlineData("file:///C:/file/d/id/view")]
    [InlineData("https://example.com/file/d/id/view")]
    [InlineData("https://drive.google.com.evil.test/file/d/id/view")]
    [InlineData("https://evil-drive.google.com/file/d/id/view")]
    [InlineData("https://www.drive.google.com/file/d/id/view")]
    [InlineData("https://drive.google.com./file/d/id/view")]
    [InlineData("https://drіve.google.com/file/d/id/view")]
    [InlineData("https://drive.google.com@evil.test/file/d/id/view")]
    [InlineData("https://user:password@drive.google.com/file/d/id/view")]
    [InlineData("https://@drive.google.com/file/d/id/view")]
    [InlineData("https://drive.google.com:8080/file/d/id/view")]
    [InlineData("https://drive.google.com:80/file/d/id/view")]
    [InlineData("https://drive.google.com\\file\\d\\id\\view")]
    [InlineData("https://drive.google.com/file/d/i\td/view")]
    [InlineData("https://drive.google.com/file/d/id with space/view")]
    [InlineData("https://drive.google.com/file/d/id%20withspace/view")]
    [InlineData("https://drive.google.com/file/d/id%0D%0A/view")]
    [InlineData("https://drive.google.com/file/d/україна/view")]
    [InlineData("https://drive.google.com/file/d/id%2Fother/view")]
    [InlineData("https://drive.google.com/file/d/id%252Fother/view")]
    [InlineData("https://drive.google.com/file/d/id%ZZ/view")]
    [InlineData("https://drive.google.com/file/d/id%/view")]
    [InlineData("https://drive.google.com/file/d//view")]
    [InlineData("https://drive.google.com/file/d/id/view/extra")]
    [InlineData("https://drive.google.com/file/d/id/download")]
    [InlineData("https://drive.google.com//file/d/id/view")]
    [InlineData("https://drive.google.com/other/../file/d/id/view")]
    [InlineData("https://drive.google.com/file/d/../id/view")]
    [InlineData("https://drive.google.com/file/d/%2E%2E/view")]
    [InlineData("https://drive.google.com/drive/folders/id")]
    [InlineData("https://drive.google.com/document/d/id/edit")]
    [InlineData("https://docs.google.com/file/d/id/view")]
    [InlineData("https://docs.google.com/forms/d/id/edit")]
    [InlineData("https://docs.google.com/document/d/e/published/pub")]
    [InlineData("https://docs.google.com/document/d/id/not-an-action")]
    [InlineData("https://docs.google.com/open?id=id")]
    [InlineData("https://drive.google.com/open")]
    [InlineData("https://drive.google.com/open?id=")]
    [InlineData("https://drive.google.com/open?id")]
    [InlineData("https://drive.google.com/open?fileId=id")]
    [InlineData("https://drive.google.com/open?id=id+other")]
    [InlineData("https://drive.google.com/open?id=id&id=other")]
    [InlineData("https://drive.google.com/open?id=id&id=id")]
    [InlineData("https://drive.google.com/open?id=id&%69d=other")]
    [InlineData("https://drive.google.com/file/d/id/view?id=other")]
    [InlineData("https://drive.google.com/file/d/id/view?id=")]
    [InlineData("https://drive.google.com/open?id=id&resourcekey=")]
    [InlineData("https://drive.google.com/open?id=id&resourcekey=key&ResourceKey=other")]
    [InlineData("https://drive.google.com/open?id=id&resourcekey=key%0D%0AInjected")]
    [InlineData("https://drive.google.com/open?id=id&resourcekey=key%2Fother")]
    [InlineData("https://drive.google.com/open?id=id&resourcekey=key%2Cother")]
    [InlineData("https://drive.google.com/open?id=id&resourcekey=key+other")]
    public void RejectsMalformedUnsupportedUntrustedAndAmbiguousLinks(string? url)
    {
        var exception = Assert.Throws<DriveLinkException>(() => _parser.Parse(url));
        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void ErrorsDoNotEchoCredentialsOrUrlContents()
    {
        var exception = Assert.Throws<DriveLinkException>(() => _parser.Parse(
            "https://synthetic-user:synthetic-password@drive.google.com/open?id=bad.id&resourcekey=synthetic-private-key"));
        Assert.DoesNotContain("synthetic-", exception.ToString());
    }
}
