using GoogleDriveDownloader.Application.Drive;

namespace GoogleDriveDownloader.Infrastructure.Drive;

internal sealed class DriveDestinationStream(Stream destination, long? limit) : Stream
{
    public long BytesWritten { get; private set; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => destination.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Flush() => destination.Flush();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Використайте асинхронний запис.");

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is { } maximum && count > maximum - BytesWritten)
            throw new DriveServiceException(DriveErrorCode.ExportTooLarge, "Експорт Google Workspace перевищує обмеження files.export у 10 МБ.");
        try
        {
            await destination.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            BytesWritten += count;
        }
        catch (IOException exception)
        {
            // Не плутаємо помилку цільового потоку з обривом читання HTTP-відповіді.
            var diskFull = (exception.HResult & 0xffff) is 0x70 or 0x27;
            throw new DriveServiceException(diskFull ? DriveErrorCode.DiskFull : DriveErrorCode.DestinationWriteFailed,
                diskFull ? "На диску недостатньо місця." : "Не вдалося записати дані в цільовий потік.");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or ObjectDisposedException or NotSupportedException)
        {
            throw new DriveServiceException(DriveErrorCode.DestinationWriteFailed, "Цільовий потік недоступний для запису.");
        }
    }
}
