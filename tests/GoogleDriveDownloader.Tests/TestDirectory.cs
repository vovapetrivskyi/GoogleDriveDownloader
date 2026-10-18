namespace GoogleDriveDownloader.Tests;

internal sealed class TestDirectory : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("GoogleDriveDownloader.Tests-");

    public string Path => _directory.FullName;

    public void Dispose() => _directory.Delete(recursive: true);
}
