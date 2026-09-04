using Microsoft.Extensions.Options;
using MyLog.Models;
using MyLog.Options;
using System.Text.RegularExpressions;

namespace MyLog.Services;

public sealed class FileBrowserService : IFileBrowserService
{
    // Tail window for "open file". The viewer shows the end of the file, which is
    // what matters for logs, without materialising an arbitrarily large string.
    private const long ContentLimit = 20L * 1024 * 1024; // 20 MB
    private const long SearchLimit = 100L * 1024 * 1024;  // 100 MB
    private const int MaxSearchResults = 2000;
    private const int MaxSearchContext = 50;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReachableCacheDuration = TimeSpan.FromSeconds(30);

    private readonly ILogger<FileBrowserService> _logger;
    private volatile IReadOnlyList<string>? _reachableRoots;
    private DateTime _reachableRootsAt;

    public FileBrowserService(IOptions<FileBrowserOptions> options, IHostEnvironment environment, ILogger<FileBrowserService> logger)
    {
        _logger = logger;
        AllowedRoots = options.Value.AllowedFolders
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Select(folder => Path.IsPathRooted(folder) ? folder : Path.Combine(environment.ContentRootPath, folder))
            .Select(folder => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (AllowedRoots.Count == 0) throw new InvalidOperationException("Configure at least one AllowedFolders path.");
    }

    public IReadOnlyList<string> AllowedRoots { get; }

    /// <summary>
    /// The configured roots that actually resolve on this host. The UI needs these
    /// to offer every allowed location - listing with no path only ever reaches
    /// inside the first root - but AllowedFolders is a shared list covering several
    /// machines, so entries that do not exist here are left out rather than shown
    /// as folders that fail the moment they are opened.
    /// </summary>
    public async Task<IReadOnlyList<FolderItemDto>> GetRootsAsync(CancellationToken cancellationToken)
    {
        var reachable = await GetReachableRootsAsync(cancellationToken);
        return reachable.Select(root => new FolderItemDto(DescribeRoot(root), root)).ToArray();
    }

                    private static string DescribeRoot(string root)
    {
        var name = Path.GetFileName(root);
        // Drive and UNC share roots have no file name component - show the path itself.
        return string.IsNullOrEmpty(name) ? root : name;
    }

    /// <summary>
    /// Probes every root, cached briefly. Probes run in parallel and are bounded by
    /// a timeout because Directory.Exists on an unreachable UNC share blocks for the
    /// SMB timeout, and the configured list contains several shares - serial probing
    /// would stall the sidebar for as long as it takes them all to give up.
    /// </summary>
    private async Task<IReadOnlyList<string>> GetReachableRootsAsync(CancellationToken cancellationToken)
    {
        var cached = _reachableRoots;
        if (cached is not null && DateTime.UtcNow - _reachableRootsAt < ReachableCacheDuration) return cached;

        var probes = AllowedRoots.Select(async root => (root, reachable: await IsReachableAsync(root)));
        var results = await Task.WhenAll(probes);

        var reachable = results.Where(result => result.reachable).Select(result => result.root).ToArray();
        if (reachable.Length == 0) _logger.LogWarning("None of the configured AllowedFolders are reachable from this host.");

        _reachableRoots = reachable;
        _reachableRootsAt = DateTime.UtcNow;
        return reachable;
    }

    private async Task<bool> IsReachableAsync(string root)
    {
        // Directory.Exists is synchronous and not cancellable, so race it against a
        // timer. A stuck probe finishes on its own later; we just stop waiting.
        var probe = Task.Run(() =>
        {
            try { return Directory.Exists(root); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        });

        if (await Task.WhenAny(probe, Task.Delay(ProbeTimeout)) == probe) return probe.Result;

        _logger.LogWarning("Timed out checking whether root {Root} is reachable.", root);
        return false;
    }

    public async Task<FileListDto> ListAsync(string? path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path)) path = await GetDefaultRootAsync(cancellationToken);
        var directory = GetSafeDirectoryPath(path);

        string? parentPath = null;
        var parent = Directory.GetParent(directory);
        if (parent is not null && IsInsideAllowedRoot(parent.FullName))
        {
            parentPath = parent.FullName;
        }

        var folders = SafeEnumerate(() => Directory.EnumerateDirectories(directory), directory, "folders")
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .Select(item => new FolderItemDto(Path.GetFileName(item), item))
            .ToArray();

        var files = SafeEnumerate(() => Directory.EnumerateFiles(directory), directory, "files")
            .Select(item => new FileInfo(item))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new FileItemDto(item.Name, item.FullName, item.Length, item.LastWriteTime, item.Extension))
            .ToArray();

        return new FileListDto(folders, files, directory, parentPath);
    }

    // A single unreadable subdirectory must not fail the whole listing.
    private IEnumerable<string> SafeEnumerate(Func<IEnumerable<string>> enumerate, string directory, string kind)
    {
        try
        {
            return enumerate().ToArray();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(exception, "Could not enumerate {Kind} in {Directory}", kind, directory);
            return [];
        }
    }

    public async Task<FileContentDto> ReadContentAsync(string path, CancellationToken cancellationToken)
    {
        var safePath = GetSafeFilePath(path);
        var info = new FileInfo(safePath);

        if (ArchiveLogReader.IsArchive(safePath))
        {
            var (archiveText, isArchiveTruncated) = await ArchiveLogReader.ReadArchiveContentAsync(safePath, ContentLimit, cancellationToken);
            return new FileContentDto(info.Name, info.Length, info.LastWriteTime, archiveText, isArchiveTruncated);
        }

        if (ArchiveLogReader.IsBinaryFile(safePath))
        {
            var binaryNotice = $"=== Binary File: {info.Name} ({ArchiveLogReader.FormatByteSize(info.Length)}) ===\n" +
                               "[Notice: This file is a binary file. Viewing binary files as plain text is not supported. Please click 'Download' to download and inspect this file.]";
            return new FileContentDto(info.Name, info.Length, info.LastWriteTime, binaryNotice, false);
        }

        await using var stream = new FileStream(safePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);

        var isTruncated = stream.Length > ContentLimit;
        if (isTruncated) stream.Position = stream.Length - ContentLimit;

        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: !isTruncated);
        // Starting mid-file almost certainly lands inside a line; drop that fragment.
        if (isTruncated) await reader.ReadLineAsync(cancellationToken);

        var text = await reader.ReadToEndAsync(cancellationToken);
        return new FileContentDto(info.Name, info.Length, info.LastWriteTime, text, isTruncated);
    }

    public async Task<IReadOnlyList<FileSearchResultDto>> SearchContentAsync(string path, string query, bool regularExpression, int context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        // Clamp caller-supplied context: it sizes an in-memory buffer per search.
        context = Math.Clamp(context, 0, MaxSearchContext);

        var safePath = GetSafeFilePath(path);
        Regex? matcher = null;
        if (regularExpression)
        {
            try
            {
                matcher = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException($"Invalid regular expression: {exception.Message}", nameof(query));
            }
        }

        if (ArchiveLogReader.IsArchive(safePath))
        {
            return await SearchArchiveContentAsync(safePath, query, matcher, context, cancellationToken);
        }

        await using var stream = new FileStream(safePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);

        var isTruncated = stream.Length > SearchLimit;
        long startOffset = 0;
        if (isTruncated)
        {
            startOffset = stream.Length - SearchLimit;
            stream.Position = startOffset;
        }

        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: !isTruncated);
        if (isTruncated) await reader.ReadLineAsync(cancellationToken);

        // When the head of the file is skipped, line 1 of what we read is not line 1
        // of the file. Count the skipped lines so reported numbers stay truthful.
        var lineNumber = isTruncated ? await CountLinesAsync(safePath, startOffset, cancellationToken) : 0;

        var results = new List<FileSearchResultDto>();
        var before = new Queue<FileSearchResultDto>();
        var addedLineNumbers = new HashSet<int>();
        var afterContextRemaining = 0;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            var isMatch = matcher is not null
                ? matcher.IsMatch(line)
                : line.Contains(query, StringComparison.OrdinalIgnoreCase);
            var current = new FileSearchResultDto(lineNumber, line, isMatch);

            CollectContext(current, context, results, before, addedLineNumbers, ref afterContextRemaining);

            if (results.Count >= MaxSearchResults) break;
        }

        return results;
    }

    private static void CollectContext(
        FileSearchResultDto current,
        int context,
        List<FileSearchResultDto> results,
        Queue<FileSearchResultDto> before,
        HashSet<int> addedLineNumbers,
        ref int afterContextRemaining)
    {
        if (current.IsMatch)
        {
            while (before.Count > 0)
            {
                var buffered = before.Dequeue();
                if (addedLineNumbers.Add(buffered.LineNumber)) results.Add(buffered);
            }
            if (addedLineNumbers.Add(current.LineNumber)) results.Add(current);
            afterContextRemaining = context;
        }
        else if (afterContextRemaining > 0)
        {
            if (addedLineNumbers.Add(current.LineNumber)) results.Add(current);
            afterContextRemaining--;
        }
        else if (context > 0)
        {
            before.Enqueue(current);
            if (before.Count > context) before.Dequeue();
        }
    }

    private static async Task<IReadOnlyList<FileSearchResultDto>> SearchArchiveContentAsync(
        string safePath,
        string query,
        Regex? matcher,
        int context,
        CancellationToken cancellationToken)
    {
        var (lines, _) = await ArchiveLogReader.ReadArchiveLinesAsync(safePath, 5000, cancellationToken);
        var results = new List<FileSearchResultDto>();
        var before = new Queue<FileSearchResultDto>();
        var addedLineNumbers = new HashSet<int>();
        var afterContextRemaining = 0;
        var lineNumber = 0;

        foreach (var line in lines)
        {
            lineNumber++;
            var isMatch = matcher is not null
                ? matcher.IsMatch(line)
                : line.Contains(query, StringComparison.OrdinalIgnoreCase);
            var current = new FileSearchResultDto(lineNumber, line, isMatch);

            CollectContext(current, context, results, before, addedLineNumbers, ref afterContextRemaining);
            if (results.Count >= MaxSearchResults) break;
        }

        return results;
    }

    public async Task<IReadOnlyList<FolderSearchResultDto>> SearchFolderAsync(
        string folderPath,
        string query,
        bool regularExpression,
        CancellationToken cancellationToken)
    {
        var safeDir = GetSafeDirectoryPath(folderPath);
        var dirInfo = new DirectoryInfo(safeDir);
        if (!dirInfo.Exists) throw new DirectoryNotFoundException("Folder not found.");

        Regex? matcher = null;
        if (regularExpression)
        {
            try
            {
                matcher = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Invalid regular expression: {ex.Message}", nameof(query));
            }
        }

        var logFiles = dirInfo.EnumerateFiles("*.*", SearchOption.TopDirectoryOnly)
            .Where(f => f.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase) ||
                        f.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
                        f.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                        f.Extension.Equals(".gz", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(25)
            .ToList();

        var results = new List<FolderSearchResultDto>();

        foreach (var file in logFiles)
        {
            if (results.Count >= 100) break;
            if (ArchiveLogReader.IsArchive(file.FullName))
            {
                await ArchiveLogReader.SearchFolderArchiveAsync(file, matcher, query, results, cancellationToken);
            }
            else
            {
                await SearchSingleFileAsync(file, matcher, query, results, cancellationToken);
            }
        }

        return results;
    }

    private static async Task SearchSingleFileAsync(
        FileInfo file,
        Regex? matcher,
        string query,
        List<FolderSearchResultDto> results,
        CancellationToken cancellationToken)
    {
        try
        {
            const long maxScan = 10L * 1024 * 1024;
            await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
            if (stream.Length > maxScan)
            {
                stream.Seek(stream.Length - maxScan, SeekOrigin.Begin);
            }

            using var reader = new StreamReader(stream);
            if (stream.Position > 0) await reader.ReadLineAsync(cancellationToken);

            int lineNumber = 0;
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                lineNumber++;
                var isMatch = matcher is not null
                    ? matcher.IsMatch(line)
                    : line.Contains(query, StringComparison.OrdinalIgnoreCase);

                if (isMatch)
                {
                    var snippet = line.Length > 120 ? line[..120] + "..." : line;
                    results.Add(new FolderSearchResultDto(file.Name, file.FullName, lineNumber, snippet));
                    if (results.Count >= 100) break;
                }
            }
        }
        catch
        {
            // Skip locked or unreadable files gracefully
        }
    }

    /// <summary>Counts the newlines in [0, offset) so truncated searches report real line numbers.</summary>
    private static async Task<int> CountLinesAsync(string path, long offset, CancellationToken cancellationToken)
    {
        if (offset <= 0) return 0;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, useAsync: true);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(128 * 1024);
        var count = 0;
        long remaining = offset;

        try
        {
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                if (read == 0) break;
                remaining -= read;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] == (byte)'\n') count++;
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        return count;
    }

    public string GetSafeFilePath(string path)
    {
        var safePath = GetSafePath(path);
        if (!File.Exists(safePath)) throw new FileNotFoundException("File was not found.");
        return safePath;
    }

    public string GetContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => "application/json",
        ".xml" => "application/xml",
        ".csv" => "text/csv",
        ".zip" => "application/zip",
        ".gz" => "application/gzip",
        ".tar" => "application/x-tar",
        ".7z" => "application/x-7z-compressed",
        // Served as a download; never as text/html, which would let a logged
        // payload execute in the browser under this origin.
        ".html" or ".htm" => "text/plain",
        _ => "text/plain"
    };

    public string GetSafeDirectoryPath(string? path)
    {
        var safePath = GetSafePath(string.IsNullOrWhiteSpace(path) ? AllowedRoots[0] : path);
        if (!Directory.Exists(safePath)) throw new DirectoryNotFoundException("Folder was not found.");
        return safePath;
    }

    /// <summary>
    /// The landing folder: the first root that actually resolves here, so a machine
    /// where entry one is missing is not greeted with "Folder was not found".
    /// </summary>
    private async Task<string> GetDefaultRootAsync(CancellationToken cancellationToken)
    {
        var reachable = await GetReachableRootsAsync(cancellationToken);
        return reachable.Count > 0 ? reachable[0] : AllowedRoots[0];
    }

    private bool IsInsideAllowedRoot(string fullPath)
    {
        var normalised = Path.TrimEndingDirectorySeparator(fullPath);
        return AllowedRoots.Any(root =>
            normalised.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            normalised.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private string GetSafePath(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Malformed input is a client error, not a 500.
            throw new UnauthorizedAccessException("The requested path is not a valid file system path.");
        }

        if (!IsInsideAllowedRoot(fullPath))
        {
            _logger.LogWarning("Rejected path outside configured roots: {Path}", path);
            // Deliberately does not echo the configured roots back to the caller.
            throw new UnauthorizedAccessException("The requested path is outside the allowed folders.");
        }

        return fullPath;
    }

    public async Task<FileTailDto> GetTailAsync(string path, long position, int maxLines, CancellationToken cancellationToken)
    {
        var safePath = GetSafeFilePath(path);
        var info = new FileInfo(safePath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Log file not found.", path);
        }

        var fileSize = info.Length;

        if (ArchiveLogReader.IsArchive(safePath))
        {
            var (archiveContent, _) = await ArchiveLogReader.ReadArchiveContentAsync(safePath, 256 * 1024, cancellationToken);
            var archiveLines = archiveContent.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            return new FileTailDto(info.Name, fileSize, fileSize, archiveContent, archiveLines, IsReset: true, HasMore: false);
        }

        const long initialTailBytes = 32L * 1024; // 32 KB initial window
        const long maxReadBytes = 512L * 1024;    // 512 KB max incremental slice

        // 1. Initial tail or file rotated / truncated
        if (position <= 0 || position > fileSize)
        {
            var startPos = Math.Max(0, fileSize - initialTailBytes);
            await using var stream = new FileStream(safePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
            stream.Position = startPos;
            var bytesToRead = (int)(fileSize - startPos);
            var buffer = new byte[bytesToRead];
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead), cancellationToken);
            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);

            // If we started mid-file, drop the first fragmented line
            if (startPos > 0)
            {
                var newlineIdx = text.IndexOf('\n');
                if (newlineIdx >= 0 && newlineIdx < text.Length - 1)
                {
                    text = text[(newlineIdx + 1)..];
                }
            }

            var lines = SplitLines(text, maxLines);
            return new FileTailDto(info.Name, fileSize, fileSize, text, lines, IsReset: true, HasMore: false);
        }

        // 2. No new data written
        if (position == fileSize)
        {
            return new FileTailDto(info.Name, position, fileSize, string.Empty, [], IsReset: false, HasMore: false);
        }

        // 3. Incremental tail read (position < fileSize)
        {
            await using var stream = new FileStream(safePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
            stream.Position = position;
            var bytesToRead = (int)Math.Min(fileSize - position, maxReadBytes);
            var buffer = new byte[bytesToRead];
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead), cancellationToken);
            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
            var newPosition = position + bytesRead;
            var lines = SplitLines(text, maxLines);
            return new FileTailDto(info.Name, newPosition, fileSize, text, lines, IsReset: false, HasMore: newPosition < fileSize);
        }
    }

    private static IReadOnlyList<string> SplitLines(string text, int maxLines)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var split = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        if (maxLines > 0 && split.Length > maxLines)
        {
            return split.TakeLast(maxLines).ToArray();
        }
        return split;
    }
}






