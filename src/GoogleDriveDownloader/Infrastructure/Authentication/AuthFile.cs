namespace GoogleDriveDownloader.Infrastructure.Authentication;

internal static class AuthFile
{
    internal static void DeleteIfPresent(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    internal static string UserDirectory(string? directory)
    {
        if (directory is null)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                throw new AuthStorageException("Каталог локальних даних Windows-користувача недоступний.");
            }

            directory = Path.Combine(localAppData, "GoogleDriveDownloader");
        }

        return Path.GetFullPath(directory);
    }

    internal static async Task<byte[]> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous);
        if (stream.Length > 1024 * 1024)
        {
            throw new InvalidDataException("Auth file exceeds size limit.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    internal static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        if (bytes.Length > 1024 * 1024)
        {
            throw new InvalidDataException("Auth file exceeds size limit.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            // Тимчасовий файл у тій самій директорії: заміна не потребує копіювання між томами.
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            DeleteIfPresent(temporaryPath);
        }
    }
}
