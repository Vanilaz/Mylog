using Microsoft.Extensions.Options;
using MyLog.Models;
using MyLog.Options;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;

namespace MyLog.Services;

public sealed class RemoteLogService : IRemoteLogService
{
    private readonly HttpClient _httpClient;
    private readonly RemoteLogOptions _options;
    private readonly ILogger<RemoteLogService> _logger;
    private readonly ILogAnalyticsService _analyticsService;

    private const long ContentLimit = 20L * 1024 * 1024; // 20 MB
    private const int MaxSearchResults = 2000;
    private const int MaxSearchContext = 50;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HashSet<string> RecognizedFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".log", ".txt", ".zip", ".gz", ".7z", ".tar"
    };

    public RemoteLogService(
        HttpClient httpClient, 
        IOptions<RemoteLogOptions> options, 
        ILogger<RemoteLogService> logger,
        ILogAnalyticsService analyticsService)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _analyticsService = analyticsService;
    }

    // ── 1. List Projects ──────────────────────────────────────────────
    // curl -L -X POST '.../ListProjectNameLogInquiry'
    //   -H 'accept: application/json' -H 'API-KEY: ...' -H 'Cookie: ...'
    // Response: array of strings  e.g. ["retailservice","otherproject"]
    public async Task<IReadOnlyList<FolderItemDto>> GetProjectsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Calling Gateway: ListProjectNameLogInquiry");

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.ListProjectsPath);
        // POST with empty body (matching the curl which has no -d flag)
        request.Content = null;

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogInformation("ListProjects response: {StatusCode}, body length: {Length}", response.StatusCode, body?.Length ?? 0);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("ListProjects failed: {StatusCode} {Body}", response.StatusCode, body);
            throw new InvalidOperationException($"Gateway returned error: {response.StatusCode} - {body}");
        }

        if (string.IsNullOrWhiteSpace(body)) return Array.Empty<FolderItemDto>();

        return ParseStringArray(body)
            .Select(name => new FolderItemDto(name, name))
            .ToArray();
    }

    // ── 2. List Files ─────────────────────────────────────────────────
    // curl -L '.../ListFileNameLogInquiry'
    //   -H 'accept: application/json' -H 'content-type: application/json'
    //   -d '{"projectName":"retailservice"}'
    // Response: array of strings  e.g. ["RetailService.log","Other.log"]
    public async Task<FileListDto> GetFilesAsync(string projectName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            throw new ArgumentException("ProjectName is required", nameof(projectName));

        _logger.LogInformation("Calling Gateway: ListFileNameLogInquiry for project {Project}", projectName);

        var payload = JsonSerializer.Serialize(new { projectName });
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.ListFilesPath)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogInformation("ListFiles response: {StatusCode}, body length: {Length}", response.StatusCode, body?.Length ?? 0);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("ListFiles failed: {StatusCode} {Body}", response.StatusCode, body);
            throw new InvalidOperationException($"Gateway returned error: {response.StatusCode} - {body}");
        }

        if (string.IsNullOrWhiteSpace(body))
            return new FileListDto(Array.Empty<FolderItemDto>(), Array.Empty<FileItemDto>(), projectName, null);

        var rawItems = ParseStringArray(body);
        var folders = new List<FolderItemDto>();
        var files = new List<FileItemDto>();

        foreach (var item in rawItems)
        {
            var ext = Path.GetExtension(item);
            if (!string.IsNullOrEmpty(ext) && RecognizedFileExtensions.Contains(ext))
            {
                files.Add(new FileItemDto(item, $"{projectName}/{item}".Replace('\\', '/'), 0, DateTime.UtcNow, ext));
            }
            else
            {
                var cleanItem = item.Trim('/', '\\');
                if (!string.IsNullOrWhiteSpace(cleanItem))
                {
                    folders.Add(new FolderItemDto(cleanItem, $"{projectName}/{cleanItem}".Replace('\\', '/')));
                }
            }
        }

        var parentPath = projectName.Contains('/') ? projectName[..projectName.LastIndexOf('/')] : null;

        return new FileListDto(folders, files, projectName, parentPath);
    }

    // ── 3. Browse File (download ZIP) ─────────────────────────────────
    // curl -L '.../BrowseFileLogByName'
    //   -H 'Content-Type: application/json'
    //   -d '{"projectName":"retailservice","fileName":"RetailService.log"}'
    // Response: ZIP binary
    public async Task<FileContentDto> BrowseFileAsync(string projectName, string fileName, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Calling Gateway: BrowseFileLogByName for {Project}/{File}", projectName, fileName);

        using var zipStream = await GetOrDownloadZipAsync(projectName, fileName, cancellationToken);
        try
        {
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

            // Pick the first .log or .txt entry, or just the first entry
            var entry = archive.Entries.FirstOrDefault(e =>
                            e.FullName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                            e.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                        ?? archive.Entries.FirstOrDefault();

            if (entry == null)
            {
                EvictFromCache(projectName, fileName);
                throw new FileNotFoundException("No files found inside the ZIP archive.");
            }

            if (entry.Length > _options.MaxExtractedSizeMB * 1024L * 1024)
                throw new InvalidOperationException($"Extracted file exceeds limit of {_options.MaxExtractedSizeMB} MB.");

            using var entryStream = entry.Open();
            using var reader = new StreamReader(entryStream, detectEncodingFromByteOrderMarks: true);
            var text = await reader.ReadToEndAsync(cancellationToken);

            var isTruncated = text.Length > ContentLimit;
            if (isTruncated)
                text = text.Substring(text.Length - (int)ContentLimit);

            _logger.LogInformation("BrowseFile success: entry={Entry}, length={Length}, truncated={Truncated}",
                entry.Name, entry.Length, isTruncated);

            return new FileContentDto(entry.Name, entry.Length, entry.LastWriteTime.DateTime, text, isTruncated);
        }
        catch (InvalidDataException ex)
        {
            EvictFromCache(projectName, fileName);
            _logger.LogError(ex, "Failed to read ZIP archive from Gateway.");
            throw new InvalidOperationException("Invalid ZIP file received from Gateway.");
        }
    }

    // ── Search (in-memory on ZIP content) ─────────────────────────────
    public async Task<IReadOnlyList<FileSearchResultDto>> SearchContentAsync(
        string projectName, string fileName, string query, bool regex, int context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<FileSearchResultDto>();
        context = Math.Clamp(context, 0, MaxSearchContext);

        Regex? matcher = null;
        if (regex)
        {
            try { matcher = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)); }
            catch (ArgumentException ex) { throw new ArgumentException($"Invalid regular expression: {ex.Message}", nameof(query)); }
        }

        using var zipStream = await GetOrDownloadZipAsync(projectName, fileName, cancellationToken);
        try
        {
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
            var entry = archive.Entries.FirstOrDefault(e =>
                            e.FullName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                            e.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                        ?? archive.Entries.FirstOrDefault();

            if (entry == null) return Array.Empty<FileSearchResultDto>();

            using var entryStream = entry.Open();
            using var reader = new StreamReader(entryStream, detectEncodingFromByteOrderMarks: true);

            var results = new List<FileSearchResultDto>();
            var before = new Queue<FileSearchResultDto>();
            var addedLineNumbers = new HashSet<int>();
            var afterContextRemaining = 0;
            var lineNumber = 0;

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
        catch (InvalidDataException ex)
        {
            EvictFromCache(projectName, fileName);
            _logger.LogError(ex, "Failed to read ZIP archive for search.");
            throw new InvalidOperationException("Invalid ZIP file received from Gateway.");
        }
    }

    // ── 4. Download Single Remote File ────────────────────────────────
    public async Task<(Stream stream, string resolvedFileName)> DownloadFileAsync(
        string projectName, string fileName, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Downloading remote file {Project}/{File}", projectName, fileName);

        using var zipStream = await GetOrDownloadZipAsync(projectName, fileName, cancellationToken);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        var entry = archive.Entries.FirstOrDefault(e =>
                        e.FullName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                        e.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    ?? archive.Entries.FirstOrDefault();

        if (entry == null)
        {
            EvictFromCache(projectName, fileName);
            throw new FileNotFoundException("No files found inside the ZIP archive.");
        }

        var tempFile = Path.Combine(Path.GetTempPath(), $"mylog_remote_file_{Guid.NewGuid():N}.tmp");
        var fs = new FileStream(tempFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.DeleteOnClose);

        await using (var entryStream = entry.Open())
        {
            await entryStream.CopyToAsync(fs, 128 * 1024, cancellationToken);
        }

        fs.Position = 0;
        var resolvedName = string.IsNullOrWhiteSpace(entry.Name) ? fileName : entry.Name;
        return (fs, resolvedName);
    }

    // ── 5. Download Entire Remote Project Folder (ZIP) ─────────────────
    public async Task<Stream> DownloadProjectZipAsync(string projectName, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Downloading remote project ZIP for {Project}", projectName);

        var fileList = await GetFilesAsync(projectName, cancellationToken);
        var tempFile = Path.Combine(Path.GetTempPath(), $"mylog_remote_proj_{Guid.NewGuid():N}.zip");
        var masterFs = new FileStream(tempFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.DeleteOnClose);

        using (var masterArchive = new ZipArchive(masterFs, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in fileList.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var zipStream = await GetOrDownloadZipAsync(projectName, file.Name, cancellationToken);
                    using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
                    var entry = archive.Entries.FirstOrDefault(e =>
                                    e.FullName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                                    e.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                                ?? archive.Entries.FirstOrDefault();

                    if (entry != null)
                    {
                        var masterEntry = masterArchive.CreateEntry($"{projectName}/{entry.Name}", CompressionLevel.Fastest);
                        await using var masterEntryStream = masterEntry.Open();
                        await using var entryStream = entry.Open();
                        await entryStream.CopyToAsync(masterEntryStream, 128 * 1024, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to include {File} in project ZIP for {Project}", file.Name, projectName);
                }
            }
        }

        masterFs.Position = 0;
        return masterFs;
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    private static readonly string CacheDir = Path.Combine(Path.GetTempPath(), "MyLogRemoteZipCache");
    private static readonly ConcurrentDictionary<string, (string FilePath, DateTime CachedAt)> ZipCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CacheGates = new(StringComparer.OrdinalIgnoreCase);

    private async Task<Stream> GetOrDownloadZipAsync(string projectName, string fileName, CancellationToken cancellationToken)
    {
        var cacheKey = $"{projectName}::{fileName}";

        if (TryGetFromCache(cacheKey, out var cachedStream))
        {
            _logger.LogInformation("Serving remote ZIP from cache for {Project}/{File}", projectName, fileName);
            return cachedStream!;
        }

        var gate = CacheGates.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetFromCache(cacheKey, out var doubleCheckStream))
            {
                _logger.LogInformation("Serving remote ZIP from cache (after lock) for {Project}/{File}", projectName, fileName);
                return doubleCheckStream!;
            }

            Directory.CreateDirectory(CacheDir);
            CleanExpiredCache();

            var cacheFile = Path.Combine(CacheDir, $"cache_{Guid.NewGuid():N}.zip");
            await DownloadZipToPathAsync(projectName, fileName, cacheFile, cancellationToken);

            ZipCache[cacheKey] = (cacheFile, DateTime.UtcNow);
            return new FileStream(cacheFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, useAsync: true);
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool TryGetFromCache(string cacheKey, out Stream? stream)
    {
        stream = null;
        if (ZipCache.TryGetValue(cacheKey, out var cached) &&
            (DateTime.UtcNow - cached.CachedAt) < CacheDuration &&
            File.Exists(cached.FilePath))
        {
            try
            {
                stream = new FileStream(cached.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, useAsync: true);
                return true;
            }
            catch (IOException)
            {
                // File busy or inaccessible, fallback to fresh download
            }
        }
        return false;
    }

    private async Task DownloadZipToPathAsync(string projectName, string fileName, string destinationPath, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { projectName, fileName });
        const int maxRetries = 3;
        var delayMs = 500;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.BrowseFilePath)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("*/*"));

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var statusCode = response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                await using (var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 128 * 1024, useAsync: true))
                {
                    await response.Content.CopyToAsync(fs, cancellationToken);
                    _logger.LogInformation("Downloaded ZIP: {Bytes} bytes (Attempt {Attempt})", fs.Length, attempt);

                    if (fs.Length > _options.MaxZipSizeMB * 1024L * 1024)
                    {
                        throw new InvalidOperationException($"ZIP file exceeds limit of {_options.MaxZipSizeMB} MB.");
                    }
                }
                return;
            }

            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("BrowseFile download failed on attempt {Attempt}: {StatusCode} {Body}", attempt, statusCode, errorBody);

            if (attempt == maxRetries)
            {
                _logger.LogError("BrowseFile download failed after {Retries} attempts.", maxRetries);
                throw new InvalidOperationException($"Gateway returned error: {statusCode} - {errorBody}");
            }

            await Task.Delay(delayMs, cancellationToken);
            delayMs *= 2;
        }

        throw new InvalidOperationException("Failed to download ZIP.");
    }

    private static void CleanExpiredCache()
    {
        var now = DateTime.UtcNow;
        foreach (var (key, (filePath, cachedAt)) in ZipCache)
        {
            if (now - cachedAt > TimeSpan.FromMinutes(2))
            {
                if (ZipCache.TryRemove(key, out _))
                {
                    try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
                }
            }
        }
    }

    internal static void ClearZipCache()
    {
        foreach (var (key, (filePath, _)) in ZipCache)
        {
            if (ZipCache.TryRemove(key, out _))
            {
                try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
            }
        }
    }

    private static void EvictFromCache(string projectName, string fileName)
    {
        var cacheKey = $"{projectName}::{fileName}";
        if (ZipCache.TryRemove(cacheKey, out var cached))
        {
            try { if (File.Exists(cached.FilePath)) File.Delete(cached.FilePath); } catch { }
        }
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

    /// <summary>
    /// Parses a JSON array that may contain plain strings or objects with a name/projectName property.
    /// Handles: ["a","b"], [{"name":"a"},{"projectName":"b"}], or even a single string.
    /// </summary>
    private List<string> ParseStringArray(string json)
    {
        var result = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                {
                    var value = ExtractElementValue(el);
                    if (!string.IsNullOrEmpty(value)) result.Add(value);
                }
            }
            else if (root.ValueKind == JsonValueKind.String)
            {
                var s = root.GetString();
                if (!string.IsNullOrEmpty(s)) result.Add(s);
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                var wrapped = FindWrappedArrayJson(root);
                if (wrapped != null)
                {
                    return ParseStringArray(wrapped);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse Gateway JSON response: {Json}", json.Length > 500 ? json[..500] : json);
            throw new InvalidOperationException("Invalid JSON response from Gateway.");
        }

        return result;
    }

    private static string? ExtractElementValue(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Object => TryGetStringProperty(el, "name")
                                 ?? TryGetStringProperty(el, "projectName")
                                 ?? TryGetStringProperty(el, "fileName")
                                 ?? TryGetStringProperty(el, "logname")
                                 ?? TryGetStringProperty(el, "logName")
                                 ?? el.ToString(),
            _ => el.ToString()
        };
    }

    private static string? FindWrappedArrayJson(JsonElement root)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Array)
                return prop.Value.GetRawText();
        }
        return null;
    }

    private static string? TryGetStringProperty(JsonElement el, string propertyName)
    {
        if (el.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    public async Task<LogAnalyticsDto> AnalyzeRemoteFileAsync(string projectName, string fileName, CancellationToken cancellationToken)
    {
        var fileContent = await BrowseFileAsync(projectName, fileName, cancellationToken);
        var lines = fileContent.Content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        return _analyticsService.AnalyzeLines(lines);
    }
}
