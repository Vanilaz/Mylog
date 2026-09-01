using Microsoft.Extensions.Options;
using MyLog.Models;
using MyLog.Options;
using System.Text.RegularExpressions;

namespace MyLog.Services;

public sealed class FileBrowserService : IFileBrowserService
{
    private const int ContentLimit = 4 * 1024 * 1024;
    private readonly ILogger<FileBrowserService> _logger;

    public FileBrowserService(IOptions<FileBrowserOptions> options, IHostEnvironment environment, ILogger<FileBrowserService> logger)
    {
        _logger = logger;
        AllowedRoots = options.Value.AllowedFolders
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Select(folder => Path.IsPathRooted(folder) ? folder : Path.Combine(environment.ContentRootPath, folder))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (AllowedRoots.Count == 0) throw new InvalidOperationException("Configure at least one AllowedFolders path.");
    }

    public IReadOnlyList<string> AllowedRoots { get; }

    public Task<FileListDto> ListAsync(string? path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = GetSafeDirectoryPath(path);
        
        string? parentPath = null;
        var parentDir = Directory.GetParent(directory);
        if (parentDir != null)
        {
            var fullParent = parentDir.FullName;
            bool isAllowed = AllowedRoots.Any(root => fullParent.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                                                      fullParent.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (isAllowed)
            {
                parentPath = fullParent;
            }
        }

        var folders = Directory.EnumerateDirectories(directory)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .Select(item => new FolderItemDto(Path.GetFileName(item), item))
            .ToArray();
        var files = Directory.EnumerateFiles(directory)
            .Select(item => new FileInfo(item))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new FileItemDto(item.Name, item.FullName, item.Length, item.LastWriteTime, item.Extension))
            .ToArray();
        return Task.FromResult(new FileListDto(folders, files, directory, parentPath));
    }

    public async Task<FileContentDto> ReadContentAsync(string path, CancellationToken cancellationToken)
    {
        var safePath = GetSafeFilePath(path);
        var info = new FileInfo(safePath);
        await using var stream = new FileStream(safePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, true);
        
        long byteLimit = ContentLimit; // 4MB
        bool isTruncated = info.Length > byteLimit;
        if (isTruncated)
        {
            stream.Position = info.Length - byteLimit;
        }

        using var reader = new StreamReader(stream);
        if (isTruncated)
        {
            // Discard the first partial line to ensure clean output
            await reader.ReadLineAsync(cancellationToken);
        }

        var text = await reader.ReadToEndAsync(cancellationToken);
        return new FileContentDto(info.Name, info.Length, info.LastWriteTime, text, isTruncated);
    }

    public async Task<IReadOnlyList<FileSearchResultDto>> SearchContentAsync(string path, string query, bool regularExpression, int context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var safePath = GetSafeFilePath(path);
        var results = new List<FileSearchResultDto>();
        var matcher = regularExpression
            ? new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2))
            : null;
        await using var stream = new FileStream(safePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, true);
        
        long searchLimit = 100 * 1024 * 1024; // Limit to last 100MB for performance
        bool isTruncated = stream.Length > searchLimit;
        if (isTruncated)
        {
            stream.Position = stream.Length - searchLimit;
        }

        using var reader = new StreamReader(stream);
        if (isTruncated)
        {
            await reader.ReadLineAsync(cancellationToken); // discard first partial line
        }

        var lineNumber = 0;
        var ringBuffer = new Queue<FileSearchResultDto>();
        int afterContextRemaining = 0;
        var addedLineNumbers = new HashSet<int>();

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            bool isMatch = matcher?.IsMatch(line) == true || matcher is null && line.Contains(query, StringComparison.OrdinalIgnoreCase);
            var currentDto = new FileSearchResultDto(lineNumber, line, isMatch);

            if (isMatch)
            {
                while (ringBuffer.Count > 0)
                {
                    var b = ringBuffer.Dequeue();
                    if (addedLineNumbers.Add(b.LineNumber)) results.Add(b);
                }
                if (addedLineNumbers.Add(currentDto.LineNumber)) results.Add(currentDto);
                afterContextRemaining = context;
            }
            else if (afterContextRemaining > 0)
            {
                if (addedLineNumbers.Add(currentDto.LineNumber)) results.Add(currentDto);
                afterContextRemaining--;
            }
            else if (context > 0)
            {
                ringBuffer.Enqueue(currentDto);
                if (ringBuffer.Count > context)
                {
                    ringBuffer.Dequeue();
                }
            }

            if (results.Count >= 500) break;
        }
        return results;
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
        ".html" or ".htm" => "text/html",
        _ => "text/plain"
    };

    private string GetSafeDirectoryPath(string? path)
    {
        var safePath = GetSafePath(string.IsNullOrWhiteSpace(path) ? AllowedRoots[0] : path);
        if (!Directory.Exists(safePath)) throw new DirectoryNotFoundException("Folder was not found.");
        return safePath;
    }

    private string GetSafePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!AllowedRoots.Any(root => fullPath.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                                      fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("Rejected path outside configured roots: {Path}", path);
            throw new UnauthorizedAccessException($"The requested path is outside the allowed folders. Allowed roots: {string.Join(", ", AllowedRoots)}");
        }
        return fullPath;
    }
}