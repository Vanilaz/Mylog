using Microsoft.Extensions.Options;
using MyLog.Models;
using MyLog.Options;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace MyLog.Services;

public sealed class ClusterLogService : IClusterLogService
{
    private const int MaxViewerLines = 3000;
    private const long ContentLimit = 20L * 1024 * 1024; // 20 MB per node
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly ILogger<ClusterLogService> _logger;
    private readonly ClusterOptions _options;
    private readonly FileBrowserOptions _browserOptions;
    private readonly HttpClient _httpClient;
    private readonly IHostEnvironment _environment;
    private readonly ILogAnalyticsService _analytics;
    private readonly IFileBrowserService _fileBrowser;

    public ClusterLogService(
        ILogger<ClusterLogService> logger,
        IOptions<ClusterOptions> options,
        IOptions<FileBrowserOptions> browserOptions,
        IHttpClientFactory httpClientFactory,
        IHostEnvironment environment,
        ILogAnalyticsService analytics,
        IFileBrowserService fileBrowser)
    {
        _logger = logger;
        _options = options.Value;
        _browserOptions = browserOptions.Value;
        _httpClient = httpClientFactory.CreateClient("ClusterPeerClient");
        _environment = environment;
        _analytics = analytics;
        _fileBrowser = fileBrowser;
    }

    public async Task<IReadOnlyList<ClusterInfoDto>> GetClustersAsync(CancellationToken cancellationToken)
    {
        var result = new List<ClusterInfoDto>();

        foreach (var cluster in _options.Clusters)
        {
            var distinctNodes = cluster.Nodes.DistinctBy(n => n.NodeId).ToList();
            var nodeTasks = distinctNodes.Select(node => ProbeNodeAsync(node, cancellationToken)).ToArray();
            var nodeResults = await Task.WhenAll(nodeTasks);

            result.Add(new ClusterInfoDto(
                cluster.ClusterId,
                cluster.ClusterName,
                cluster.Description,
                nodeResults
            ));
        }

        return result;
    }

    public async Task<ClusterFileListDto> BrowseClusterPathAsync(
        string clusterId,
        string? relativePath,
        CancellationToken cancellationToken)
    {
        var cluster = GetCluster(clusterId);
        var cleanRelPath = CleanRelativePath(relativePath);

        var nodeTasks = cluster.Nodes.Select(node => ProbeAndBrowseNodeAsync(node, cleanRelPath, cancellationToken)).ToArray();
        var nodeResults = await Task.WhenAll(nodeTasks);

        var nodeDtos = nodeResults.Select(r => r.NodeDto).ToList();
        var folderDict = new Dictionary<string, ClusterFolderItemDto>(StringComparer.OrdinalIgnoreCase);
        var fileDict = new Dictionary<string, (long MaxSize, DateTime LastModified, List<string> NodeIds, List<string> NodeNames)>(StringComparer.OrdinalIgnoreCase);

        foreach (var res in nodeResults)
        {
            if (!res.NodeDto.IsReachable) continue;

            foreach (var folder in res.Folders)
            {
                if (!folderDict.ContainsKey(folder.Name))
                {
                    folderDict[folder.Name] = folder;
                }
            }

            foreach (var file in res.Files)
            {
                if (!fileDict.TryGetValue(file.Name, out var existing))
                {
                    fileDict[file.Name] = (file.Size, file.LastModified, [res.NodeDto.NodeId], [res.NodeDto.NodeName]);
                }
                else
                {
                    var maxSize = Math.Max(existing.MaxSize, file.Size);
                    var latestMod = file.LastModified > existing.LastModified ? file.LastModified : existing.LastModified;
                    existing.NodeIds.Add(res.NodeDto.NodeId);
                    existing.NodeNames.Add(res.NodeDto.NodeName);
                    fileDict[file.Name] = (maxSize, latestMod, existing.NodeIds, existing.NodeNames);
                }
            }
        }

        var files = fileDict
            .OrderBy(f => f.Key)
            .Select(f => new ClusterFileItemDto(
                f.Key,
                Path.Combine(cleanRelPath, f.Key).Replace('\\', '/'),
                f.Value.MaxSize,
                f.Value.LastModified,
                f.Value.NodeIds,
                f.Value.NodeNames,
                Path.GetExtension(f.Key)))
            .ToList();

        var folders = folderDict.Values.OrderBy(f => f.Name).ToList();
        var parentPath = string.IsNullOrEmpty(cleanRelPath) ? null : Path.GetDirectoryName(cleanRelPath)?.Replace('\\', '/');

        return new ClusterFileListDto(
            cluster.ClusterId,
            cleanRelPath,
            parentPath,
            folders,
            files,
            nodeDtos
        );
    }

    public async Task<ClusterFileContentDto> ReadClusterFileAsync(
        string clusterId,
        string? relativePath,
        string fileName,
        string? selectedNodeId,
        string? viewMode,
        CancellationToken cancellationToken)
    {
        var cluster = GetCluster(clusterId);
        var cleanRelPath = CleanRelativePath(relativePath);
        var isAllNodes = string.IsNullOrWhiteSpace(selectedNodeId) || selectedNodeId.Equals("all", StringComparison.OrdinalIgnoreCase);

        var targetNodes = isAllNodes
            ? cluster.Nodes
            : cluster.Nodes.Where(n => n.NodeId.Equals(selectedNodeId, StringComparison.OrdinalIgnoreCase)).ToList();

        if (targetNodes.Count == 0)
        {
            throw new ArgumentException($"Node with ID '{selectedNodeId}' was not found in cluster '{clusterId}'.");
        }

        var readTasks = targetNodes.Select(node => ReadNodeFileAsync(node, cleanRelPath, fileName, cancellationToken)).ToArray();
        var nodeResults = await Task.WhenAll(readTasks);

        var allLines = new List<ClusterFileLineDto>();
        var nodesDto = new List<ClusterNodeDto>();
        var isTruncated = false;

        foreach (var (node, lines, truncated, reachable, err) in nodeResults)
        {
            nodesDto.Add(new ClusterNodeDto(node.NodeId, node.NodeName, reachable, err));
            if (truncated) isTruncated = true;
            allLines.AddRange(lines);
        }

        var effectiveViewMode = string.Equals(viewMode, "concat", StringComparison.OrdinalIgnoreCase) ? "concat" : "timeline";
        var totalLines = allLines.Count;

        if (isAllNodes && allLines.Count > 0)
        {
            if (effectiveViewMode == "concat")
            {
                var (concatLines, concatTruncated) = PrepareConcatLines(nodeResults, MaxViewerLines);
                allLines = concatLines;
                if (concatTruncated) isTruncated = true;
            }
            else
            {
                allLines = allLines
                    .OrderBy(l => l.Timestamp ?? DateTime.MinValue)
                    .ThenBy(l => l.LineNumber)
                    .ToList();

                if (allLines.Count > MaxViewerLines)
                {
                    allLines = allLines.Skip(allLines.Count - MaxViewerLines).ToList();
                    isTruncated = true;
                }
            }
        }
        else if (allLines.Count > MaxViewerLines)
        {
            allLines = allLines.Skip(allLines.Count - MaxViewerLines).ToList();
            isTruncated = true;
        }

        return new ClusterFileContentDto(
            cluster.ClusterId,
            fileName,
            isAllNodes ? "all" : selectedNodeId,
            isAllNodes,
            nodesDto,
            allLines,
            isTruncated,
            totalLines,
            effectiveViewMode
        );
    }

    public Task<ClusterFileContentDto> ReadClusterFileAsync(
        string clusterId,
        string? relativePath,
        string fileName,
        string? selectedNodeId,
        CancellationToken cancellationToken)
    {
        return ReadClusterFileAsync(clusterId, relativePath, fileName, selectedNodeId, "timeline", cancellationToken);
    }

    public async Task<LogAnalyticsDto> AnalyzeClusterFileAsync(
        string clusterId,
        string? relativePath,
        string fileName,
        string? selectedNodeId,
        CancellationToken cancellationToken)
    {
        var content = await ReadClusterFileAsync(clusterId, relativePath, fileName, selectedNodeId, "timeline", cancellationToken);
        return _analytics.AnalyzeLines(content.Lines.Select(l => l.Line));
    }

    public async Task<IReadOnlyList<ClusterSearchResultDto>> SearchClusterAsync(
        string clusterId,
        string? relativePath,
        string fileName,
        string query,
        bool regex,
        int context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var cluster = GetCluster(clusterId);
        var cleanRelPath = CleanRelativePath(relativePath);

        Regex? matcher = null;
        if (regex)
        {
            try { matcher = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)); }
            catch (ArgumentException ex) { throw new ArgumentException($"Invalid regular expression: {ex.Message}", nameof(query)); }
        }

        var searchTasks = cluster.Nodes.Select(node => SearchNodeAsync(node, cleanRelPath, fileName, query, matcher, context, cancellationToken)).ToArray();
        var searchResults = await Task.WhenAll(searchTasks);

        var combined = searchResults.SelectMany(r => r).ToList();
        return combined;
    }

    public async Task<IReadOnlyList<ClusterFolderSearchResultDto>> SearchClusterFolderAsync(
        string clusterId,
        string? relativePath,
        string query,
        bool regex,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var cluster = GetCluster(clusterId);
        var cleanRelPath = CleanRelativePath(relativePath);

        Regex? matcher = null;
        if (regex)
        {
            try { matcher = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)); }
            catch (ArgumentException ex) { throw new ArgumentException($"Invalid regular expression: {ex.Message}", nameof(query)); }
        }

        var searchTasks = cluster.Nodes.Select(node => SearchNodeFolderAsync(node, cleanRelPath, query, matcher, cancellationToken)).ToArray();
        var searchResults = await Task.WhenAll(searchTasks);

        return searchResults.SelectMany(r => r).Take(200).ToList();
    }


    public async Task<Stream> DownloadClusterZipAsync(
        string clusterId,
        string? relativePath,
        string fileName,
        CancellationToken cancellationToken)
    {
        var cluster = GetCluster(clusterId);
        var cleanRelPath = CleanRelativePath(relativePath);

        var tempFile = Path.Combine(Path.GetTempPath(), $"mylog_cluster_{Guid.NewGuid():N}.zip");
        var fs = new FileStream(tempFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.DeleteOnClose);

        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var node in cluster.Nodes)
            {
                var safeNodeFolder = node.NodeName.Replace(" ", "_").Replace(":", "_").Replace("\\", "_");

                if (IsLocalNode(node))
                {
                    var localPath = Path.Combine(GetNodeLocalPath(node), cleanRelPath, fileName);
                    if (File.Exists(localPath))
                    {
                        var entry = archive.CreateEntry($"{safeNodeFolder}/{fileName}", CompressionLevel.Fastest);
                        await using var entryStream = entry.Open();
                        await using var nodeFileStream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
                        await nodeFileStream.CopyToAsync(entryStream, cancellationToken);
                        continue;
                    }
                }

                if (!string.IsNullOrWhiteSpace(node.ApiUrl))
                {
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
                    try
                    {
                        var url = $"{node.ApiUrl.TrimEnd('/')}/api/cluster/node/download?path={Uri.EscapeDataString(cleanRelPath)}&fileName={Uri.EscapeDataString(fileName)}";
                        using var req = new HttpRequestMessage(HttpMethod.Get, url);
                        if (!string.IsNullOrWhiteSpace(node.HostHeader)) req.Headers.Host = node.HostHeader;
                        var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
                        if (resp.IsSuccessStatusCode)
                        {
                            var entry = archive.CreateEntry($"{safeNodeFolder}/{fileName}", CompressionLevel.Fastest);
                            await using var entryStream = entry.Open();
                            await using var peerStream = await resp.Content.ReadAsStreamAsync(linkedCts.Token);
                            await peerStream.CopyToAsync(entryStream, linkedCts.Token);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to download peer zip from {Url}", node.ApiUrl);
                    }
                }
            }
        }

        fs.Position = 0;
        return fs;
    }

    public async Task<Stream> DownloadClusterFolderZipAsync(
        string clusterId,
        string? relativePath,
        CancellationToken cancellationToken)
    {
        var cluster = GetCluster(clusterId);
        var cleanRelPath = CleanRelativePath(relativePath);

        var fs = ZipArchiveHelper.CreateTempZipStream($"mylog_cluster_folder_{clusterId}");

        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var node in cluster.Nodes)
            {
                var safeNodeFolder = node.NodeName.Replace(" ", "_").Replace(":", "_").Replace("\\", "_");

                if (IsLocalNode(node))
                {
                    var localDirPath = Path.Combine(GetNodeLocalPath(node), cleanRelPath);
                    if (Directory.Exists(localDirPath))
                    {
                        await ArchiveLocalNodeDirectoryAsync(archive, safeNodeFolder, localDirPath, cancellationToken);
                        continue;
                    }
                }

                if (!string.IsNullOrWhiteSpace(node.ApiUrl))
                {
                    await DownloadPeerFolderZipAsync(archive, safeNodeFolder, node, cleanRelPath, cancellationToken);
                }
            }
        }

        fs.Position = 0;
        return fs;
    }

    private static async Task ArchiveLocalNodeDirectoryAsync(
        ZipArchive archive, string nodeFolder, string localDirPath, CancellationToken cancellationToken)
    {
        string[] files = [];
        try { files = Directory.GetFiles(localDirPath, "*", SearchOption.AllDirectories); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(localDirPath, file).Replace('\\', '/');
            var entry = archive.CreateEntry($"{nodeFolder}/{relativePath}", CompressionLevel.Fastest);

            try
            {
                await using var entryStream = entry.Open();
                await using var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
                await fileStream.CopyToAsync(entryStream, 64 * 1024, cancellationToken);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task DownloadPeerFolderZipAsync(
        ZipArchive masterArchive, string nodeFolder, ClusterNodeConfig node, string cleanRelPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(node.ApiUrl)) return;
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
        try
        {
            var url = $"{node.ApiUrl.TrimEnd('/')}/api/cluster/node/download-folder?path={Uri.EscapeDataString(cleanRelPath)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(node.HostHeader)) req.Headers.Host = node.HostHeader;
            var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
            if (resp.IsSuccessStatusCode)
            {
                await using var peerStream = await resp.Content.ReadAsStreamAsync(linkedCts.Token);
                using var peerArchive = new ZipArchive(peerStream, ZipArchiveMode.Read);
                foreach (var peerEntry in peerArchive.Entries)
                {
                    if (string.IsNullOrEmpty(peerEntry.Name)) continue;
                    var masterEntry = masterArchive.CreateEntry($"{nodeFolder}/{peerEntry.FullName.TrimStart('/')}", CompressionLevel.Fastest);
                    await using var masterStream = masterEntry.Open();
                    await using var entryStream = peerEntry.Open();
                    await entryStream.CopyToAsync(masterStream, 64 * 1024, linkedCts.Token);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download peer folder zip from {Url}", node.ApiUrl);
        }
    }

    public async Task<Stream> DownloadClusterConcatLogAsync(
        string clusterId,
        string? relativePath,
        string fileName,
        CancellationToken cancellationToken)
    {
        var cluster = GetCluster(clusterId);
        var cleanRelPath = CleanRelativePath(relativePath);

        var tempFile = Path.Combine(Path.GetTempPath(), $"mylog_cluster_concat_{Guid.NewGuid():N}.log");
        var fs = new FileStream(tempFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.DeleteOnClose);
        await using (var writer = new StreamWriter(fs, Encoding.UTF8, 128 * 1024, leaveOpen: true))
        {
            foreach (var node in cluster.Nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (targetNode, lines, _, reachable, err) = await ReadNodeFileAsync(node, cleanRelPath, fileName, cancellationToken);

                await writer.WriteLineAsync($"================================================================================");
                await writer.WriteLineAsync($"=== Node: {targetNode.NodeName} ({targetNode.NodeId}) {(reachable ? "" : "[OFFLINE/ERROR]")}");
                await writer.WriteLineAsync($"================================================================================");

                if (!reachable || lines.Count == 0)
                {
                    await writer.WriteLineAsync(err ?? "(No log lines found on this node)");
                    await writer.WriteLineAsync();
                    continue;
                }

                foreach (var line in lines.OrderBy(l => l.LineNumber))
                {
                    await writer.WriteLineAsync(line.Line);
                }
                await writer.WriteLineAsync();
            }
        }

        fs.Position = 0;
        return fs;
    }

    // ── Local Node Peer Handlers (Invoked by Peer Nodes) ─────────────────

    public Task<ClusterNodeBrowseResult> LocalNodeBrowseAsync(string? relativePath, CancellationToken cancellationToken)
    {
        var cleanRelPath = CleanRelativePath(relativePath);
        var rootDir = GetLocalRootDirectory(cleanRelPath);

        if (!Directory.Exists(rootDir))
        {
            return Task.FromResult(new ClusterNodeBrowseResult([], []));
        }

        var folders = Directory.GetDirectories(rootDir)
            .Select(d => new ClusterFolderItemDto(Path.GetFileName(d), Path.Combine(cleanRelPath, Path.GetFileName(d)).Replace('\\', '/')))
            .ToList();

        var files = Directory.GetFiles(rootDir)
            .Where(f => f.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            .Select(f => {
                var fi = new FileInfo(f);
                return new FileItemDto(fi.Name, Path.Combine(cleanRelPath, fi.Name).Replace('\\', '/'), fi.Length, fi.LastWriteTime, fi.Extension);
            })
            .ToList();

        return Task.FromResult(new ClusterNodeBrowseResult(folders, files));
    }

    public async Task<ClusterNodeContentResult> LocalNodeContentAsync(string? relativePath, string fileName, CancellationToken cancellationToken)
    {
        var cleanRelPath = CleanRelativePath(relativePath);
        var rootDir = GetLocalRootDirectory(cleanRelPath);
        var filePath = Path.Combine(rootDir, fileName);

        if (!File.Exists(filePath))
        {
            return new ClusterNodeContentResult([], false, 0);
        }

        if (ArchiveLogReader.IsArchive(filePath))
        {
            var (archiveLines, isArchiveTruncated) = await ArchiveLogReader.ReadArchiveLinesAsync(filePath, MaxViewerLines, cancellationToken);
            var mappedLines = new List<ClusterFileLineDto>();
            var archLineNum = 0;
            DateTime? archLastSeenTime = null;

            foreach (var rawLine in archiveLines)
            {
                archLineNum++;
                var parsedTime = TryParseLogTimestamp(rawLine);
                if (parsedTime != null) archLastSeenTime = parsedTime;
                mappedLines.Add(new ClusterFileLineDto(archLineNum, rawLine, "", "", parsedTime ?? archLastSeenTime));
            }

            return new ClusterNodeContentResult(mappedLines, isArchiveTruncated, mappedLines.Count);
        }

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
        var isTruncated = stream.Length > ContentLimit;
        if (isTruncated) stream.Position = stream.Length - ContentLimit;

        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: !isTruncated);
        if (isTruncated) await reader.ReadLineAsync(cancellationToken);

        var lines = new List<ClusterFileLineDto>();
        var lineNum = 0;
        DateTime? lastSeenTime = null;

        while (await reader.ReadLineAsync(cancellationToken) is { } rawLine)
        {
            lineNum++;
            var parsedTime = TryParseLogTimestamp(rawLine);
            if (parsedTime != null) lastSeenTime = parsedTime;

            lines.Add(new ClusterFileLineDto(lineNum, rawLine, "", "", parsedTime ?? lastSeenTime));
        }

        return new ClusterNodeContentResult(lines, isTruncated, lines.Count);
    }

    public async Task<List<ClusterSearchResultDto>> LocalNodeSearchAsync(string? relativePath, string fileName, string query, bool regex, int context, CancellationToken cancellationToken)
    {
        var cleanRelPath = CleanRelativePath(relativePath);
        var rootDir = GetLocalRootDirectory(cleanRelPath);
        var filePath = Path.Combine(rootDir, fileName);

        if (!File.Exists(filePath)) return [];

        Regex? matcher = null;
        if (regex)
        {
            try { matcher = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)); }
            catch { return []; }
        }

        var results = new List<ClusterSearchResultDto>();
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

        var lineNum = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNum++;
            var isMatch = matcher != null
                ? matcher.IsMatch(line)
                : line.Contains(query, StringComparison.OrdinalIgnoreCase);

            if (isMatch)
            {
                results.Add(new ClusterSearchResultDto(lineNum, line, "", "", true));
            }
        }

        return results;
    }

    public Task<List<ClusterFolderSearchResultDto>> LocalNodeSearchFolderAsync(string? relativePath, string query, bool regex, CancellationToken cancellationToken)
    {
        var cleanRelPath = CleanRelativePath(relativePath);
        Regex? matcher = null;
        if (regex)
        {
            try { matcher = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)); }
            catch { return Task.FromResult(new List<ClusterFolderSearchResultDto>()); }
        }

        var localNode = new ClusterNodeConfig("local", "Local Node", GetLocalRootDirectory(""));
        return SearchLocalNodeFolderInternalAsync(localNode, cleanRelPath, query, matcher, cancellationToken);
    }

    public Task<Stream?> LocalNodeDownloadAsync(string? relativePath, string fileName, CancellationToken cancellationToken)
    {
        var cleanRelPath = CleanRelativePath(relativePath);
        var rootDir = GetLocalRootDirectory(cleanRelPath);
        var filePath = Path.Combine(rootDir, fileName);

        if (!File.Exists(filePath)) return Task.FromResult<Stream?>(null);

        Stream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
        return Task.FromResult<Stream?>(stream);
    }

    public async Task<Stream?> LocalNodeDownloadFolderAsync(string? relativePath, CancellationToken cancellationToken)
    {
        var cleanRelPath = CleanRelativePath(relativePath);
        var rootDir = GetLocalRootDirectory(cleanRelPath);

        if (!Directory.Exists(rootDir)) return null;

        var fs = ZipArchiveHelper.CreateTempZipStream("mylog_node_folder");
        await ZipArchiveHelper.ArchiveDirectoryAsync(rootDir, fs, cancellationToken);
        fs.Position = 0;
        return fs;
    }

    // ── Internal Helpers ──────────────────────────────────────────────────

    private ClusterConfig GetCluster(string clusterId)
    {
        var cluster = _options.Clusters.FirstOrDefault(c => c.ClusterId.Equals(clusterId, StringComparison.OrdinalIgnoreCase));
        if (cluster == null) throw new KeyNotFoundException($"Cluster with ID '{clusterId}' not found.");
        var distinctNodes = cluster.Nodes.DistinctBy(n => n.NodeId).ToList();
        return cluster with { Nodes = distinctNodes };
    }

    private static string CleanRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return string.Empty;
        if (relativePath.Contains("..")) throw new ArgumentException("Path traversal (..) is not allowed.");
        return relativePath.Trim('/', '\\');
    }

    private string GetNodeFullPath(ClusterNodeConfig node)
    {
        return Path.IsPathRooted(node.Path)
            ? Path.GetFullPath(node.Path)
            : Path.GetFullPath(Path.Combine(_environment.ContentRootPath, node.Path));
    }

    private string GetLocalRootDirectory(string cleanRelPath)
    {
        // Try D:\WSProjectLogs or first allowed folder
        var root = _browserOptions.AllowedFolders.FirstOrDefault(f => f.StartsWith("D:", StringComparison.OrdinalIgnoreCase))
                   ?? _browserOptions.AllowedFolders.FirstOrDefault()
                   ?? Directory.GetCurrentDirectory();

        return string.IsNullOrEmpty(cleanRelPath) ? root : Path.Combine(root, cleanRelPath);
    }

    private bool IsLocalNode(ClusterNodeConfig node)
    {
        if (!node.Path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            var uriHost = string.Empty;
            if (!string.IsNullOrWhiteSpace(node.ApiUrl) && Uri.TryCreate(node.ApiUrl, UriKind.Absolute, out var uri))
            {
                uriHost = uri.Host;
            }
            else
            {
                var parts = node.Path.TrimStart('\\').Split('\\', 2);
                if (parts.Length > 0) uriHost = parts[0];
            }

            if (string.Equals(uriHost, "localhost", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uriHost, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uriHost, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var hostAddresses = System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName());
            foreach (var ip in hostAddresses)
            {
                if (string.Equals(uriHost, ip.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to resolve host for node {NodeId}", node.NodeId);
        }

        return false;
    }

    private string GetNodeLocalPath(ClusterNodeConfig node)
    {
        if (node.Path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var trimmed = node.Path.TrimStart('\\');
            var slashIdx = trimmed.IndexOf('\\');
            if (slashIdx >= 0)
            {
                var afterHost = trimmed[(slashIdx + 1)..];
                var shareParts = afterHost.Split('\\', 2);
                var share = shareParts[0];
                if (share.Length == 2 && char.IsLetter(share[0]) && share[1] == '$')
                {
                    var drive = char.ToUpperInvariant(share[0]);
                    var rest = shareParts.Length > 1 ? shareParts[1] : string.Empty;
                    var localDrivePath = $"{drive}:\\{rest}";
                    if (Directory.Exists(localDrivePath))
                    {
                        return localDrivePath;
                    }
                }
            }

            var localRoot = _browserOptions.AllowedFolders.FirstOrDefault(f => !f.StartsWith(@"\\", StringComparison.Ordinal) && Directory.Exists(f));
            if (localRoot != null) return localRoot;
        }

        return GetNodeFullPath(node);
    }

    private async Task<ClusterNodeDto> ProbeNodeAsync(ClusterNodeConfig node, CancellationToken cancellationToken)
    {
        // 1. Direct local check (0ms, no network)
        if (IsLocalNode(node))
        {
            var localPath = GetNodeLocalPath(node);
            if (Directory.Exists(localPath))
            {
                return new ClusterNodeDto(node.NodeId, node.NodeName, true, "Ready (Local)");
            }
        }

        // 2. HTTP Peer Check with bounded 2.5s timeout
        if (!string.IsNullOrWhiteSpace(node.ApiUrl))
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
            try
            {
                var targetUrl = $"{node.ApiUrl.TrimEnd('/')}/api/cluster/node/probe";
                using var req = new HttpRequestMessage(HttpMethod.Get, targetUrl);
                if (!string.IsNullOrWhiteSpace(node.HostHeader)) req.Headers.Host = node.HostHeader;

                var resp = await _httpClient.SendAsync(req, linkedCts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    return new ClusterNodeDto(node.NodeId, node.NodeName, true, "Ready (HTTP Peer)");
                }
                return new ClusterNodeDto(node.NodeId, node.NodeName, false, $"HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Cluster probe failed for node {NodeId} ({Url}): {Message}", node.NodeId, node.ApiUrl, ex.Message);
                return new ClusterNodeDto(node.NodeId, node.NodeName, false, "Node unreachable (offline)");
            }
        }

        // 3. UNC share check bounded by Task.WhenAny (never blocks request thread)
        if (node.Path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var probe = Task.Run(() => Directory.Exists(node.Path));
            if (await Task.WhenAny(probe, Task.Delay(1500, cancellationToken)) == probe && probe.Result)
            {
                return new ClusterNodeDto(node.NodeId, node.NodeName, true, "Ready (UNC Share)");
            }
        }

        return new ClusterNodeDto(node.NodeId, node.NodeName, false, "Node unreachable (offline)");
    }

    private async Task<(ClusterNodeDto NodeDto, List<ClusterFolderItemDto> Folders, List<FileItemDto> Files)> ProbeAndBrowseNodeAsync(
        ClusterNodeConfig node,
        string cleanRelPath,
        CancellationToken cancellationToken)
    {
        // 1. Direct local disk
        if (IsLocalNode(node))
        {
            try
            {
                var targetDir = Path.Combine(GetNodeLocalPath(node), cleanRelPath);
                if (Directory.Exists(targetDir))
                {
                    var folders = Directory.GetDirectories(targetDir)
                        .Select(d => new ClusterFolderItemDto(Path.GetFileName(d), Path.Combine(cleanRelPath, Path.GetFileName(d)).Replace('\\', '/')))
                        .ToList();

                    var files = Directory.GetFiles(targetDir)
                        .Where(f => f.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                        .Select(f => {
                            var fi = new FileInfo(f);
                            return new FileItemDto(fi.Name, Path.Combine(cleanRelPath, fi.Name).Replace('\\', '/'), fi.Length, fi.LastWriteTime, fi.Extension);
                        })
                        .ToList();

                    return (new ClusterNodeDto(node.NodeId, node.NodeName, true, "Ready (Local)"), folders, files);
                }
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Local browse error on node {NodeId}", node.NodeId);
            }
        }

        // 2. HTTP Peer with bounded 2.5s timeout
        if (!string.IsNullOrWhiteSpace(node.ApiUrl))
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
            try
            {
                var targetUrl = $"{node.ApiUrl.TrimEnd('/')}/api/cluster/node/browse?path={Uri.EscapeDataString(cleanRelPath)}";
                using var req = new HttpRequestMessage(HttpMethod.Get, targetUrl);
                if (!string.IsNullOrWhiteSpace(node.HostHeader)) req.Headers.Host = node.HostHeader;

                var resp = await _httpClient.SendAsync(req, linkedCts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    var data = await resp.Content.ReadFromJsonAsync<ClusterNodeBrowseResult>(cancellationToken: linkedCts.Token);
                    if (data != null)
                    {
                        return (new ClusterNodeDto(node.NodeId, node.NodeName, true, "Ready (HTTP Peer)"), data.Folders, data.Files);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Remote cluster node {NodeId} ({Url}) browse error: {Message}", node.NodeId, node.ApiUrl, ex.Message);
                return (new ClusterNodeDto(node.NodeId, node.NodeName, false, "Node unreachable (offline)"), [], []);
            }
        }

        return (new ClusterNodeDto(node.NodeId, node.NodeName, false, "Node unreachable (offline)"), [], []);
    }

    private async Task<(ClusterNodeConfig Node, List<ClusterFileLineDto> Lines, bool Truncated, bool Reachable, string? Error)> ReadNodeFileAsync(
        ClusterNodeConfig node,
        string cleanRelPath,
        string fileName,
        CancellationToken cancellationToken)
    {
        // 1. Direct local file access
        if (IsLocalNode(node))
        {
            var filePath = Path.Combine(GetNodeLocalPath(node), cleanRelPath, fileName);
            if (File.Exists(filePath))
            {
                try
                {
                    if (ArchiveLogReader.IsArchive(filePath))
                    {
                        var (archiveLines, isArchiveTruncated) = await ArchiveLogReader.ReadArchiveLinesAsync(filePath, MaxViewerLines, cancellationToken);
                        var mappedLines = new List<ClusterFileLineDto>();
                        var archLineNum = 0;
                        DateTime? archLastSeenTime = null;

                        foreach (var rawLine in archiveLines)
                        {
                            archLineNum++;
                            var parsedTime = TryParseLogTimestamp(rawLine);
                            if (parsedTime != null) archLastSeenTime = parsedTime;
                            mappedLines.Add(new ClusterFileLineDto(archLineNum, rawLine, node.NodeId, node.NodeName, parsedTime ?? archLastSeenTime));
                        }

                        return (node, mappedLines, isArchiveTruncated, true, null);
                    }

                    await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
                    var isTruncated = stream.Length > ContentLimit;
                    if (isTruncated) stream.Position = stream.Length - ContentLimit;

                    using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: !isTruncated);
                    if (isTruncated) await reader.ReadLineAsync(cancellationToken);

                    var lines = new List<ClusterFileLineDto>();
                    var lineNum = 0;
                    DateTime? lastSeenTime = null;

                    while (await reader.ReadLineAsync(cancellationToken) is { } rawLine)
                    {
                        lineNum++;
                        var parsedTime = TryParseLogTimestamp(rawLine);
                        if (parsedTime != null) lastSeenTime = parsedTime;

                        lines.Add(new ClusterFileLineDto(lineNum, rawLine, node.NodeId, node.NodeName, parsedTime ?? lastSeenTime));
                    }

                    return (node, lines, isTruncated, true, null);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read local node file {Path}", filePath);
                }
            }
        }

        // 2. HTTP Peer fallback with bounded 5s timeout
        if (!string.IsNullOrWhiteSpace(node.ApiUrl))
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
            try
            {
                var targetUrl = $"{node.ApiUrl.TrimEnd('/')}/api/cluster/node/content?path={Uri.EscapeDataString(cleanRelPath)}&fileName={Uri.EscapeDataString(fileName)}";
                using var req = new HttpRequestMessage(HttpMethod.Get, targetUrl);
                if (!string.IsNullOrWhiteSpace(node.HostHeader)) req.Headers.Host = node.HostHeader;

                var resp = await _httpClient.SendAsync(req, linkedCts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    var data = await resp.Content.ReadFromJsonAsync<ClusterNodeContentResult>(cancellationToken: linkedCts.Token);
                    if (data != null)
                    {
                        var mappedLines = data.Lines
                            .Select(l => new ClusterFileLineDto(l.LineNumber, l.Line, node.NodeId, node.NodeName, l.Timestamp))
                            .ToList();
                        return (node, mappedLines, data.IsTruncated, true, null);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to read remote node content from {Url}: {Message}", node.ApiUrl, ex.Message);
                return (node, [], false, false, $"Node offline: {ex.Message}");
            }
        }

        return (node, [], false, false, "File not accessible on this node.");
    }

    private async Task<List<ClusterSearchResultDto>> SearchNodeAsync(
        ClusterNodeConfig node,
        string cleanRelPath,
        string fileName,
        string query,
        Regex? matcher,
        int context,
        CancellationToken cancellationToken)
    {
        // 1. Direct local file search
        if (IsLocalNode(node))
        {
            var filePath = Path.Combine(GetNodeLocalPath(node), cleanRelPath, fileName);
            if (File.Exists(filePath))
            {
                var results = new List<ClusterSearchResultDto>();
                try
                {
                    await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
                    using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

                    var lineNum = 0;
                    while (await reader.ReadLineAsync(cancellationToken) is { } line)
                    {
                        lineNum++;
                        var isMatch = matcher != null
                            ? matcher.IsMatch(line)
                            : line.Contains(query, StringComparison.OrdinalIgnoreCase);

                        if (isMatch)
                        {
                            results.Add(new ClusterSearchResultDto(lineNum, line, node.NodeId, node.NodeName, true));
                        }
                    }
                    return results;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to search local node file {Path}", filePath);
                }
            }
        }

        // 2. HTTP Peer search with bounded 5s timeout
        if (!string.IsNullOrWhiteSpace(node.ApiUrl))
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
            try
            {
                var targetUrl = $"{node.ApiUrl.TrimEnd('/')}/api/cluster/node/search?path={Uri.EscapeDataString(cleanRelPath)}&fileName={Uri.EscapeDataString(fileName)}&query={Uri.EscapeDataString(query)}&regex={matcher != null}&context={context}";
                using var req = new HttpRequestMessage(HttpMethod.Get, targetUrl);
                if (!string.IsNullOrWhiteSpace(node.HostHeader)) req.Headers.Host = node.HostHeader;

                var resp = await _httpClient.SendAsync(req, linkedCts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    var data = await resp.Content.ReadFromJsonAsync<List<ClusterSearchResultDto>>(cancellationToken: linkedCts.Token);
                    if (data != null)
                    {
                        return data.Select(r => r with { NodeId = node.NodeId, NodeName = node.NodeName }).ToList();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to search remote node at {Url}: {Message}", node.ApiUrl, ex.Message);
            }
        }

        return [];
    }

    private async Task<List<ClusterFolderSearchResultDto>> SearchNodeFolderAsync(
        ClusterNodeConfig node,
        string cleanRelPath,
        string query,
        Regex? matcher,
        CancellationToken cancellationToken)
    {
        if (IsLocalNode(node))
        {
            return await SearchLocalNodeFolderInternalAsync(node, cleanRelPath, query, matcher, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(node.ApiUrl))
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
            try
            {
                var targetUrl = $"{node.ApiUrl.TrimEnd('/')}/api/cluster/node/search-folder?path={Uri.EscapeDataString(cleanRelPath)}&query={Uri.EscapeDataString(query)}&regex={matcher != null}";
                using var req = new HttpRequestMessage(HttpMethod.Get, targetUrl);
                if (!string.IsNullOrWhiteSpace(node.HostHeader)) req.Headers.Host = node.HostHeader;

                var resp = await _httpClient.SendAsync(req, linkedCts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    var data = await resp.Content.ReadFromJsonAsync<List<ClusterFolderSearchResultDto>>(cancellationToken: linkedCts.Token);
                    if (data != null)
                    {
                        return data.Select(r => r with { NodeId = node.NodeId, NodeName = node.NodeName }).ToList();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to search remote node folder at {Url}: {Message}", node.ApiUrl, ex.Message);
            }
        }

        return [];
    }

    private async Task<List<ClusterFolderSearchResultDto>> SearchLocalNodeFolderInternalAsync(
        ClusterNodeConfig node,
        string cleanRelPath,
        string query,
        Regex? matcher,
        CancellationToken cancellationToken)
    {
        var localDir = Path.Combine(GetNodeLocalPath(node), cleanRelPath);
        if (!Directory.Exists(localDir)) return [];

        var results = new List<ClusterFolderSearchResultDto>();
        try
        {
            var files = Directory.GetFiles(localDir)
                .Where(f => f.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(25)
                .ToList();

            foreach (var file in files)
            {
                if (results.Count >= 100) break;
                var fileName = Path.GetFileName(file);
                if (ArchiveLogReader.IsArchive(file))
                {
                    await SearchArchiveFileLinesAsync(file, fileName, cleanRelPath, node, query, matcher, results, cancellationToken);
                }
                else
                {
                    await SearchFileLinesAsync(file, fileName, cleanRelPath, node, query, matcher, results, cancellationToken);
                }
            }
        }
        catch (Exception)
        {
            // Ignore directory read errors
        }

        return results;
    }

    private static async Task SearchArchiveFileLinesAsync(
        string fullPath,
        string fileName,
        string cleanRelPath,
        ClusterNodeConfig node,
        string query,
        Regex? matcher,
        List<ClusterFolderSearchResultDto> results,
        CancellationToken cancellationToken)
    {
        var tempResults = new List<FolderSearchResultDto>();
        await ArchiveLogReader.SearchFolderArchiveAsync(new FileInfo(fullPath), matcher, query, tempResults, cancellationToken);
        foreach (var r in tempResults)
        {
            results.Add(new ClusterFolderSearchResultDto(
                r.FileName,
                cleanRelPath,
                r.LineNumber,
                r.Line,
                node.NodeId,
                node.NodeName));
            if (results.Count >= 100) break;
        }
    }

    private static async Task SearchFileLinesAsync(
        string fullPath,
        string fileName,
        string cleanRelPath,
        ClusterNodeConfig node,
        string query,
        Regex? matcher,
        List<ClusterFolderSearchResultDto> results,
        CancellationToken cancellationToken)
    {
        try
        {
            const long maxScan = 10L * 1024 * 1024;
            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
            if (stream.Length > maxScan) stream.Position = stream.Length - maxScan;

            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var lineNum = 0;
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                lineNum++;
                var isMatch = matcher != null ? matcher.IsMatch(line) : line.Contains(query, StringComparison.OrdinalIgnoreCase);
                if (isMatch)
                {
                    results.Add(new ClusterFolderSearchResultDto(
                        fileName,
                        Path.Combine(cleanRelPath, fileName).Replace('\\', '/'),
                        lineNum,
                        line,
                        node.NodeId,
                        node.NodeName));

                    if (results.Count >= 100) break;
                }
            }
        }
        catch { }
    }

    public async Task<FileTailDto> TailClusterFileAsync(
        string clusterId,
        string? relativePath,
        string fileName,
        string? selectedNodeId,
        long position,
        int maxLines,
        CancellationToken cancellationToken)
    {
        var cluster = GetCluster(clusterId);
        var cleanRelPath = CleanRelativePath(relativePath);

        var targetNode = cluster.Nodes.FirstOrDefault(n =>
            !string.IsNullOrWhiteSpace(selectedNodeId) && !selectedNodeId.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? n.NodeId.Equals(selectedNodeId, StringComparison.OrdinalIgnoreCase)
                : IsLocalNode(n))
            ?? cluster.Nodes.FirstOrDefault();

        if (targetNode == null)
        {
            return new FileTailDto(fileName, position, 0, string.Empty, [], false, false);
        }

        if (IsLocalNode(targetNode))
        {
            var fullPath = Path.Combine(GetNodeLocalPath(targetNode), cleanRelPath, fileName);
            return await _fileBrowser.GetTailAsync(fullPath, position, maxLines, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(targetNode.ApiUrl))
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3.5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
            try
            {
                var targetUrl = $"{targetNode.ApiUrl.TrimEnd('/')}/api/files/tail?path={Uri.EscapeDataString(Path.Combine(cleanRelPath, fileName))}&position={position}&maxLines={maxLines}";
                using var req = new HttpRequestMessage(HttpMethod.Get, targetUrl);
                if (!string.IsNullOrWhiteSpace(targetNode.HostHeader)) req.Headers.Host = targetNode.HostHeader;

                var resp = await _httpClient.SendAsync(req, linkedCts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    var data = await resp.Content.ReadFromJsonAsync<FileTailDto>(cancellationToken: linkedCts.Token);
                    if (data != null) return data;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to tail remote cluster node {Url}: {Message}", targetNode.ApiUrl, ex.Message);
            }
        }

        return new FileTailDto(fileName, position, 0, string.Empty, [], false, false);
    }

    private static DateTime? TryParseLogTimestamp(string line)
    {
        if (line.Length < 19) return null;

        if (char.IsDigit(line[0]) && char.IsDigit(line[1]) && char.IsDigit(line[2]) && char.IsDigit(line[3]) && line[4] == '-')
        {
            var match = Regex.Match(line, @"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?");
            if (match.Success && DateTime.TryParse(match.Value, out var dt))
            {
                return dt;
            }
        }

        var timeMatch = Regex.Match(line, @"^\d{2}:\d{2}:\d{2}(?:\.\d+)?");
        if (timeMatch.Success && DateTime.TryParse(timeMatch.Value, out var timeOnly))
        {
            return timeOnly;
        }

        return null;
    }

    private static (List<ClusterFileLineDto> Lines, bool Truncated) PrepareConcatLines(
        IEnumerable<(ClusterNodeConfig Node, List<ClusterFileLineDto> Lines, bool Truncated, bool Reachable, string? Error)> nodeResults,
        int maxLines)
    {
        var activeNodes = nodeResults.Where(r => r.Lines.Count > 0).ToList();
        if (activeNodes.Count == 0) return (new List<ClusterFileLineDto>(), false);

        var totalLines = activeNodes.Sum(r => r.Lines.Count);
        if (totalLines <= maxLines)
        {
            var unTruncated = activeNodes.SelectMany(r => r.Lines.OrderBy(l => l.LineNumber)).ToList();
            return (unTruncated, false);
        }

        var sorted = activeNodes.OrderBy(r => r.Lines.Count).ToList();
        var nodeAllocations = new Dictionary<string, int>();
        var remainingBudget = maxLines;
        var remainingNodes = sorted.Count;

        foreach (var item in sorted)
        {
            var fairShare = remainingBudget / remainingNodes;
            var count = Math.Min(item.Lines.Count, fairShare);
            nodeAllocations[item.Node.NodeId] = count;
            remainingBudget -= count;
            remainingNodes--;
        }

        var result = new List<ClusterFileLineDto>();
        foreach (var item in activeNodes)
        {
            var alloc = nodeAllocations[item.Node.NodeId];
            IEnumerable<ClusterFileLineDto> lines = item.Lines.OrderBy(l => l.LineNumber);
            if (item.Lines.Count > alloc)
            {
                lines = lines.Skip(item.Lines.Count - alloc);
            }
            result.AddRange(lines);
        }

        return (result, true);
    }
}
