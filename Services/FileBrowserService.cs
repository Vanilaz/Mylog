using Microsoft.Extensions.Options;
using MyLog.Models;
using MyLog.Options;
using System.Text.RegularExpressions;

namespace MyLog.Services;

public sealed class FileBrowserService : IFileBrowserService
{
    // Tail window for "open file". The viewer shows the end of the file, which is
    // what matters for logs, without materialising an arbitrarily large string.
    private const long ContentLimit = 100L * 1024 * 1024; // 100 MB
    private const long SearchLimit = 100L * 1024 * 1024;  // 100 MB
    private const int MaxSearchResults = 2000;
    private const int MaxSearchContext = 50;

    private readonly ILogger<FileBrowserService> _logger;

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
    /// The configured roots themselves. The UI needs these to offer every allowed
    /// location: listing with no path only ever reaches inside the first root.
    /// </summary>
    public IReadOnlyList<FolderItemDto> GetRoots() => AllowedRoots
        .Select(root => new FolderItemDto(DescribeRoot(root), root))
        .ToArray();

    private static string DescribeRoot(string root)
    {
        var name = Path.GetFileName(root);
        // Drive and UNC share roots have no file name component - show the path itself.
        return string.IsNullOrEmpty(name) ? root : name;
    }

    public Task<FileListDto> ListAsync(string? path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

        return Task.FromResult(new FileListDto(folders, files, directory, parentPath));
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

            if (isMatch)
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

            if (results.Count >= MaxSearchResults) break;
        }

        return results;
    }

    /// <summary>Counts the newlines in [0, offset) so truncated searches report real line numbers.</summary>
    private static async Task<int> CountLinesAsync(string path, long offset, CancellationToken cancellationToken)
    {
        if (offset <= 0) return 0;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, useAsync: true);
        var buffer = new byte[1024 * 1024];
        var count = 0;
        long remaining = offset;

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
        // Served as a download; never as text/html, which would let a logged
        // payload execute in the browser under this origin.
        ".html" or ".htm" => "text/plain",
        _ => "text/plain"
    };

    private string GetSafeDirectoryPath(string? path)
    {
        var safePath = GetSafePath(string.IsNullOrWhiteSpace(path) ? GetDefaultRoot() : path);
        if (!Directory.Exists(safePath)) throw new DirectoryNotFoundException("Folder was not found.");
        return safePath;
    }

    /// <summary>
    /// The landing folder. AllowedFolders is a shared list covering several machines,
    /// so the first entry often does not exist on this one; pick the first root that
    /// actually resolves rather than greeting the user with "Folder was not found".
    /// </summary>
    private string GetDefaultRoot()
    {
        foreach (var root in AllowedRoots)
        {
            try
            {
                if (Directory.Exists(root)) return root;
            }
            catch (IOException exception)
            {
                // An unreachable network share must not stop us trying the next root.
                _logger.LogDebug(exception, "Root {Root} is not reachable", root);
            }
        }

        _logger.LogWarning("None of the configured AllowedFolders exist on this host.");
        return AllowedRoots[0];
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
}
