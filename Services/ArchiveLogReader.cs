using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using MyLog.Models;

namespace MyLog.Services;

public static class ArchiveLogReader
{
    private const int BufferSize = 64 * 1024;

    private static readonly HashSet<string> KnownBinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".so", ".dylib", ".bin", ".dat", ".db", ".sqlite", ".iso", ".img",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp",
        ".pdf", ".docx", ".xlsx", ".pptx", ".zip", ".tar", ".7z", ".rar"
    };

    public static bool IsArchive(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".gz", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return HasArchiveSignature(filePath);
    }

    public static bool HasArchiveSignature(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 4) return false;
            Span<byte> header = stackalloc byte[4];
            var read = fs.Read(header);
            if (read < 2) return false;

            if (header[0] == 0x1F && header[1] == 0x8B) return true;

            if (read >= 4 && header[0] == 0x50 && header[1] == 0x4B &&
                ((header[2] == 0x03 && header[3] == 0x04) ||
                 (header[2] == 0x05 && header[3] == 0x06) ||
                 (header[2] == 0x07 && header[3] == 0x08)))
            {
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsBinaryFile(string filePath)
    {
        try
        {
            var ext = Path.GetExtension(filePath);
            if (KnownBinaryExtensions.Contains(ext)) return true;

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var checkLength = (int)Math.Min(fs.Length, 8192);
            if (checkLength == 0) return false;

            var buffer = new byte[checkLength];
            var bytesRead = fs.Read(buffer, 0, checkLength);
            for (var i = 0; i < bytesRead; i++)
            {
                if (buffer[i] == 0) return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<(string Content, bool IsTruncated)> ReadArchiveContentAsync(
        string filePath,
        long contentLimit,
        CancellationToken cancellationToken)
    {
        var ext = Path.GetExtension(filePath);
        if (ext.Equals(".gz", StringComparison.OrdinalIgnoreCase))
        {
            return await ReadGzContentAsync(filePath, contentLimit, cancellationToken);
        }

        return await ReadZipContentAsync(filePath, contentLimit, cancellationToken);
    }

    private static async Task<(string Content, bool IsTruncated)> ReadZipContentAsync(
        string zipPath,
        long contentLimit,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(zipPath);
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entries = archive.Entries
                .Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\'))
                .ToList();

            if (entries.Count == 0)
            {
                return ($"=== Compressed Archive: {fileName} ===\n[Notice: The archive is empty (0 files).]", false);
            }

            var textEntries = entries.Where(e => !KnownBinaryExtensions.Contains(Path.GetExtension(e.Name))).ToList();
            if (textEntries.Count == 0)
            {
                var entryList = string.Join(", ", entries.Take(5).Select(e => e.Name));
                return ($"=== Compressed Archive: {fileName} ===\n[Notice: This archive contains binary files ({entryList}). Text viewing is not supported. Please click 'Download' to inspect.]", false);
            }

            var sb = new StringBuilder();
            var totalBytesRead = 0L;
            var isTruncated = false;

            for (var i = 0; i < textEntries.Count; i++)
            {
                var entry = textEntries[i];
                var entrySizeStr = FormatByteSize(entry.Length);
                sb.AppendLine($"=== Archive: {fileName} | Entry {i + 1}/{textEntries.Count}: {entry.FullName} ({entrySizeStr}) ===");

                await using var entryStream = entry.Open();
                using var reader = new StreamReader(entryStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, BufferSize, leaveOpen: true);

                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    totalBytesRead += Encoding.UTF8.GetByteCount(line) + 1;
                    if (totalBytesRead > contentLimit)
                    {
                        isTruncated = true;
                        sb.AppendLine("\n... [Notice: Decompressed content truncated at 2 MB display limit] ...");
                        break;
                    }
                    sb.AppendLine(line);
                }

                if (isTruncated || i >= 10) break;
            }

            return (sb.ToString(), isTruncated);
        }
        catch (InvalidDataException)
        {
            return ($"=== Archive: {fileName} ===\n[Notice: Archive could not be read. File may be corrupted or encrypted. Please click 'Download' to inspect.]", false);
        }
    }

    private static async Task<(string Content, bool IsTruncated)> ReadGzContentAsync(
        string gzPath,
        long contentLimit,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(gzPath);
        try
        {
            await using var fs = new FileStream(gzPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, useAsync: true);
            await using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var reader = new StreamReader(gz, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, BufferSize);

            var sb = new StringBuilder();
            sb.AppendLine($"=== Compressed Log (GZip): {fileName} ===");
            var totalBytesRead = 0L;
            var isTruncated = false;

            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                totalBytesRead += Encoding.UTF8.GetByteCount(line) + 1;
                if (totalBytesRead > contentLimit)
                {
                    isTruncated = true;
                    sb.AppendLine("\n... [Notice: Decompressed content truncated at 2 MB display limit] ...");
                    break;
                }
                sb.AppendLine(line);
            }

            return (sb.ToString(), isTruncated);
        }
        catch (InvalidDataException)
        {
            return ($"=== Compressed Log: {fileName} ===\n[Notice: GZip file could not be decompressed. File may be corrupted.]", false);
        }
    }

    public static async Task SearchFolderArchiveAsync(
        FileInfo file,
        Regex? matcher,
        string query,
        List<FolderSearchResultDto> results,
        CancellationToken cancellationToken)
    {
        try
        {
            using var archive = ZipFile.OpenRead(file.FullName);
            var textEntries = archive.Entries
                .Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\') && !KnownBinaryExtensions.Contains(Path.GetExtension(e.Name)))
                .Take(5)
                .ToList();

            foreach (var entry in textEntries)
            {
                if (results.Count >= 100) break;
                await using var entryStream = entry.Open();
                using var reader = new StreamReader(entryStream, Encoding.UTF8);

                var lineNum = 0;
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    lineNum++;
                    var isMatch = matcher is not null
                        ? matcher.IsMatch(line)
                        : line.Contains(query, StringComparison.OrdinalIgnoreCase);

                    if (isMatch)
                    {
                        var snippet = line.Length > 120 ? line[..120] + "..." : line;
                        var displayFileName = $"{file.Name} ({entry.Name})";
                        results.Add(new FolderSearchResultDto(displayFileName, file.FullName, lineNum, snippet));
                        if (results.Count >= 100) break;
                    }
                }
            }
        }
        catch
        {
            // Skip unreadable archives gracefully
        }
    }

    public static async Task<(List<string> Lines, bool IsTruncated)> ReadArchiveLinesAsync(
        string filePath,
        int maxLines,
        CancellationToken cancellationToken)
    {
        var (content, isTruncated) = await ReadArchiveContentAsync(filePath, 2L * 1024 * 1024, cancellationToken);
        var lines = new List<string>();
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
            if (lines.Count >= maxLines)
            {
                isTruncated = true;
                break;
            }
        }
        return (lines, isTruncated);
    }

    public static string FormatByteSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F2} MB";
    }
}
