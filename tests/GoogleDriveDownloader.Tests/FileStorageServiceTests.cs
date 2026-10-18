using GoogleDriveDownloader.Application.Logging;
using GoogleDriveDownloader.Application.Storage;
using GoogleDriveDownloader.Application.Drive;
using GoogleDriveDownloader.Infrastructure.Storage;

namespace GoogleDriveDownloader.Tests;

public sealed class FileStorageServiceTests
{
    private static readonly byte[] Data = [0x10, 0x20, 0x30, 0x40];
    private static Task WriteAsync(Stream stream, CancellationToken token) => stream.WriteAsync(Data, token).AsTask();

    [Fact]
    public async Task WritesOwnPartThenClosesStreamBeforeMovingAndReturnsFinalPath()
    {
        using var directory = new TestDirectory();
        var service = new StorageTestService(new StorageTestLog());
        string? part = null;
        service.BeforeMove = (source, target) =>
        {
            Assert.Equal(part, source);
            Assert.False(File.Exists(target));
            using var exclusive = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.Equal(Data.Length, exclusive.Length);
        };
        var final = await service.SaveAsync(directory.Path, "Звіт.pdf", async (stream, token) =>
        {
            part = Assert.IsType<FileStream>(stream).Name;
            Assert.EndsWith(".part", part);
            Assert.Equal(directory.Path, Path.GetDirectoryName(part));
            Assert.False(File.Exists(Path.Combine(directory.Path, "Звіт.pdf")));
            await WriteAsync(stream, token);
        });
        Assert.Equal(Path.Combine(directory.Path, "Звіт.pdf"), final);
        Assert.Equal(Data, await File.ReadAllBytesAsync(final));
        Assert.False(File.Exists(part));
        Assert.Equal([final], Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task ExistingFilesCaseVariantsAndDirectoriesReceiveSuffixesWithoutOverwrite()
    {
        using var directory = new TestDirectory();
        var original = Path.Combine(directory.Path, "REPORT.PDF");
        await File.WriteAllTextAsync(original, "existing-data");
        Directory.CreateDirectory(Path.Combine(directory.Path, "report (2).pdf"));
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "report (3).PDF"), "third-existing");
        var service = new FileStorageService(new FileNameResolver(), new StorageTestLog());
        var final = await service.SaveAsync(directory.Path, "report.pdf", WriteAsync);
        Assert.Equal("report (4).pdf", Path.GetFileName(final));
        Assert.Equal("existing-data", await File.ReadAllTextAsync(original));
        Assert.Equal("third-existing", await File.ReadAllTextAsync(Path.Combine(directory.Path, "report (3).PDF")));
        Assert.True(Directory.Exists(Path.Combine(directory.Path, "report (2).pdf")));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.part"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollisionAppearingBetweenCheckAndMoveCannotOverwriteFileOrDirectory(bool directoryCollision)
    {
        using var directory = new TestDirectory();
        var service = new StorageTestService(new StorageTestLog());
        service.BeforeMove = (_, target) =>
        {
            if (service.MoveCalls != 1) return;
            if (directoryCollision) Directory.CreateDirectory(target);
            else File.WriteAllText(target, "created-by-other-process");
        };
        var final = await service.SaveAsync(directory.Path, "report.pdf", WriteAsync);
        Assert.Equal("report (2).pdf", Path.GetFileName(final));
        Assert.Equal(2, service.MoveCalls);
        if (directoryCollision) Assert.True(Directory.Exists(Path.Combine(directory.Path, "report.pdf")));
        else Assert.Equal("created-by-other-process", await File.ReadAllTextAsync(Path.Combine(directory.Path, "report.pdf")));
        Assert.Equal(Data, await File.ReadAllBytesAsync(final));
    }

    [Fact]
    public async Task ConcurrentInstancesCannotOverwriteTheSameFinalName()
    {
        using var directory = new TestDirectory();
        var tasks = Enumerable.Range(0, 12).Select(index =>
        {
            var service = new FileStorageService(new FileNameResolver(), new StorageTestLog());
            return service.SaveAsync(directory.Path, "report.pdf", (stream, token) => stream.WriteAsync(new byte[] { (byte)index }, token).AsTask());
        });
        var paths = await Task.WhenAll(tasks);
        Assert.Equal(12, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var contents = await Task.WhenAll(paths.Select(path => File.ReadAllBytesAsync(path)));
        Assert.Equal(Enumerable.Range(0, 12), contents.Select(bytes => (int)Assert.Single(bytes)).Order());
        Assert.Empty(Directory.GetFiles(directory.Path, "*.part"));
    }

    [Fact]
    public async Task ProducerFailurePreservesTypedDriveErrorAndRemovesOnlyOwnPartialFile()
    {
        using var directory = new TestDirectory();
        var unrelated = Path.Combine(directory.Path, "another-job.part");
        await File.WriteAllTextAsync(unrelated, "unrelated");
        var expected = new DriveServiceException(DriveErrorCode.NetworkFailure, "Synthetic transfer failure", isTransient: true);
        var service = new FileStorageService(new FileNameResolver(), new StorageTestLog());
        var actual = await Assert.ThrowsAsync<DriveServiceException>(() => service.SaveAsync(directory.Path, "report.pdf", async (stream, token) =>
        {
            await WriteAsync(stream, token);
            throw expected;
        }));
        Assert.Same(expected, actual);
        Assert.Equal([unrelated], Directory.GetFiles(directory.Path));
        Assert.Equal("unrelated", await File.ReadAllTextAsync(unrelated));
    }

    [Fact]
    public async Task CancellationDuringWriteCleansPartUsingUncancelledCleanup()
    {
        using var directory = new TestDirectory();
        var service = new FileStorageService(new FileNameResolver(), new StorageTestLog());
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = service.SaveAsync(directory.Path, "report.pdf", async (stream, token) =>
        {
            await WriteAsync(stream, token);
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task CancellationAfterProducerReturnsStillPreventsCommit()
    {
        using var directory = new TestDirectory();
        var service = new StorageTestService(new StorageTestLog());
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveAsync(directory.Path, "report.pdf", async (stream, token) =>
        {
            await WriteAsync(stream, token);
            cancellation.Cancel();
        }, cancellationToken: cancellation.Token));
        Assert.Equal(0, service.MoveCalls);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task PreCancellationDoesNotOpenDirectoryOrInvokeProducer()
    {
        var service = new StorageTestService(new StorageTestLog());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveAsync("missing", "report.pdf",
            (_, _) => throw new InvalidOperationException("Should not be called"), cancellationToken: cancellation.Token));
        Assert.Equal(0, service.OpenCalls);
    }

    [Fact]
    public async Task FailedCommitNeverLeavesFinalFileOrOwnPart()
    {
        using var directory = new TestDirectory();
        var service = new StorageTestService(new StorageTestLog()) { MoveException = new IOException("DO-NOT-EXPOSE commit-details") };
        var exception = await Assert.ThrowsAsync<FileStorageException>(() => service.SaveAsync(directory.Path, "report.pdf", WriteAsync));
        Assert.Equal(FileStorageErrorCode.IoFailure, exception.Code);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("DO-NOT-EXPOSE", exception.ToString());
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupFailureIsLoggedWithoutMaskingOriginalFailureOrCancellation(bool cancelled)
    {
        using var directory = new TestDirectory();
        var log = new StorageTestLog();
        var service = new StorageTestService(log) { FailCleanup = true };
        var context = new LogContext(RowNumber: 7, DocumentName: "Документ", FileId: "File_7");
        var expected = new DriveServiceException(DriveErrorCode.ExportTooLarge, "Synthetic export error");
        using var cancellation = new CancellationTokenSource();
        async Task Produce(Stream stream, CancellationToken token)
        {
            await WriteAsync(stream, token);
            if (cancelled)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            throw expected;
        }
        if (cancelled)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveAsync(directory.Path, "report.pdf", Produce, context, cancellation.Token));
        else
            Assert.Same(expected, await Assert.ThrowsAsync<DriveServiceException>(() => service.SaveAsync(directory.Path, "report.pdf", Produce, context)));
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogSeverity.Warning, entry.Level);
        Assert.Equal("PartialFileCleanupFailed", entry.Context.Status);
        Assert.Equal(7, entry.Context.RowNumber);
        Assert.Equal("File_7", entry.Context.FileId);
        Assert.Null(entry.ExceptionDetails);
        Assert.DoesNotContain("DO-NOT-EXPOSE", entry.Message);
        Assert.EndsWith(".part", Assert.Single(Directory.GetFiles(directory.Path)));
        Assert.False(File.Exists(Path.Combine(directory.Path, "report.pdf")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateFailureDistinguishesDiskFullAndUnavailableDestination(bool diskFull)
    {
        using var directory = new TestDirectory();
        var service = new StorageTestService(new StorageTestLog())
        {
            OpenException = diskFull ? new IOException("DO-NOT-EXPOSE", unchecked((int)0x80070070)) : new UnauthorizedAccessException("DO-NOT-EXPOSE")
        };
        var exception = await Assert.ThrowsAsync<FileStorageException>(() => service.SaveAsync(directory.Path, "report.pdf", WriteAsync));
        Assert.Equal(diskFull ? FileStorageErrorCode.DiskFull : FileStorageErrorCode.DestinationUnavailable, exception.Code);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("DO-NOT-EXPOSE", exception.ToString());
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task TemporaryCreateCollisionDoesNotDeleteOrOverwriteExistingPart()
    {
        using var directory = new TestDirectory();
        var service = new StorageTestService(new StorageTestLog());
        string? occupied = null;
        service.BeforeOpen = (path, attempt) =>
        {
            if (attempt != 1) return;
            occupied = path;
            File.WriteAllText(path, "other-application-part");
        };
        var final = await service.SaveAsync(directory.Path, "report.pdf", WriteAsync);
        Assert.Equal(2, service.OpenCalls);
        Assert.Equal("other-application-part", await File.ReadAllTextAsync(occupied!));
        Assert.Equal(Data, await File.ReadAllBytesAsync(final));
        Assert.Equal(2, Directory.GetFiles(directory.Path).Length);
    }

    [Theory]
    [InlineData("../outside.pdf")]
    [InlineData("..\\outside.pdf")]
    [InlineData("C:\\outside.pdf")]
    [InlineData("NUL.pdf")]
    [InlineData("name.pdf:alternate-stream")]
    public async Task UnresolvedTraversalOrDeviceNameIsRejectedBeforeCreatingFiles(string fileName)
    {
        using var directory = new TestDirectory();
        var service = new StorageTestService(new StorageTestLog());
        Assert.Equal(FileStorageErrorCode.InvalidName, (await Assert.ThrowsAsync<FileStorageException>(() => service.SaveAsync(directory.Path, fileName, WriteAsync))).Code);
        Assert.Equal(0, service.OpenCalls);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task ResolvedTraversalInputStaysWithinChosenDirectory()
    {
        using var directory = new TestDirectory();
        var names = new FileNameResolver();
        var metadata = new DriveFileMetadata("File", "source.pdf", "application/pdf", "pdf", 4, false, true);
        var safeName = names.Resolve("..\\..\\outside.txt", metadata, new DriveTransferFormat("application/pdf"));
        var service = new FileStorageService(names, new StorageTestLog());
        var final = await service.SaveAsync(directory.Path, safeName, WriteAsync);
        Assert.Equal(directory.Path, Path.GetDirectoryName(final));
        Assert.Equal([final], Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task LongNameAndItsDuplicateFitFullPathBudgetAndKeepExtension()
    {
        using var directory = new TestDirectory();
        var names = new FileNameResolver();
        var name = names.Resolve(new string('Я', 400), new DriveFileMetadata("File", "source.pdf", "application/pdf", "pdf", 4, false, true), new DriveTransferFormat("application/pdf"));
        var service = new FileStorageService(names, new StorageTestLog());
        var first = await service.SaveAsync(directory.Path, name, WriteAsync);
        var second = await service.SaveAsync(directory.Path, name, WriteAsync);
        Assert.InRange(first.Length, 1, FileStorageService.MaximumFullPathLength);
        Assert.InRange(second.Length, 1, FileStorageService.MaximumFullPathLength);
        Assert.EndsWith(".pdf", first);
        Assert.EndsWith(" (2).pdf", second);
        Assert.NotEqual(first, second);
        Assert.Equal(Data, await File.ReadAllBytesAsync(first));
    }

    [Fact]
    public async Task ExcessivelyLongChosenDirectoryFailsBeforeCreatingPart()
    {
        using var directory = new TestDirectory();
        var root = Path.Combine(directory.Path, new string('d', 160));
        Directory.CreateDirectory(root);
        var service = new StorageTestService(new StorageTestLog());
        var exception = await Assert.ThrowsAsync<FileStorageException>(() => service.SaveAsync(root, "report.pdf", WriteAsync));
        Assert.Equal(FileStorageErrorCode.PathTooLong, exception.Code);
        Assert.Equal(0, service.OpenCalls);
        Assert.Empty(Directory.GetFiles(root));
    }

    [Fact]
    public async Task MissingDirectoryOrFileInPlaceOfDirectoryHasSafeError()
    {
        using var directory = new TestDirectory();
        var service = new StorageTestService(new StorageTestLog());
        var path = Path.Combine(directory.Path, "missing");
        Assert.Equal(FileStorageErrorCode.DestinationUnavailable, (await Assert.ThrowsAsync<FileStorageException>(() => service.SaveAsync(path, "report.pdf", WriteAsync))).Code);
        await File.WriteAllTextAsync(path, "keep");
        Assert.Equal(FileStorageErrorCode.DestinationUnavailable, (await Assert.ThrowsAsync<FileStorageException>(() => service.SaveAsync(path, "report.pdf", WriteAsync))).Code);
        Assert.Equal("keep", await File.ReadAllTextAsync(path));
    }
}
