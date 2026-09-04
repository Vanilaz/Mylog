using System.IO.Compression;

namespace MyLog.Services;

public static class ZipArchiveHelper
{
    private const int BufferSize = 1024 * 1024; // 1 MB buffer for high throughput

    public static FileStream CreateTempZipStream(string prefix = "mylog_")
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}.zip");
        return new FileStream(tempFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.DeleteOnClose);
    }

    public static async Task ArchiveDirectoryAsync(string sourceDir, Stream outputStream, CancellationToken cancellationToken)
    {
        using var bufferedResponse = new BufferedStream(outputStream, BufferSize);
        using var archive = new ZipArchive(bufferedResponse, ZipArchiveMode.Create, leaveOpen: true);
        var pending = new Queue<string>();
        pending.Enqueue(sourceDir);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentDir = pending.Dequeue();

            await AddFilesToArchiveAsync(sourceDir, currentDir, archive, cancellationToken);
            EnqueueSubdirectories(currentDir, pending);
        }
    }

    public static async Task AddFileToArchiveAsync(ZipArchive archive, string entryName, Stream sourceStream, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await sourceStream.CopyToAsync(entryStream, BufferSize, cancellationToken);
    }

    private static async Task AddFilesToArchiveAsync(string rootDir, string currentDir, ZipArchive archive, CancellationToken cancellationToken)
    {
        string[] files = [];
        try
        {
            files = Directory.GetFiles(currentDir);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(rootDir, file).Replace('\\', '/');
            var entry = archive.CreateEntry(relativePath, CompressionLevel.NoCompression);

            try
            {
                await using var entryStream = entry.Open();
                await using var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, useAsync: true);
                await fileStream.CopyToAsync(entryStream, BufferSize, cancellationToken);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void EnqueueSubdirectories(string currentDir, Queue<string> pending)
    {
        string[] dirs = [];
        try
        {
            dirs = Directory.GetDirectories(currentDir);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        foreach (var dir in dirs)
        {
            pending.Enqueue(dir);
        }
    }
}
