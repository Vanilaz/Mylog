using MyLog.Models;

namespace MyLog.Services;

public interface IClusterLogService
{
    Task<IReadOnlyList<ClusterInfoDto>> GetClustersAsync(CancellationToken cancellationToken);

    Task<ClusterFileListDto> BrowseClusterPathAsync(string clusterId, string? relativePath, CancellationToken cancellationToken);

    Task<ClusterFileContentDto> ReadClusterFileAsync(
        string clusterId,
        string? relativePath,
        string fileName,
        string? selectedNodeId,
        string? viewMode,
        CancellationToken cancellationToken);

    Task<ClusterFileContentDto> ReadClusterFileAsync(
        string clusterId,
        string? relativePath,
        string fileName,
        string? selectedNodeId,
        CancellationToken cancellationToken);

    Task<LogAnalyticsDto> AnalyzeClusterFileAsync(
        string clusterId,
        string? relativePath,
        string fileName,
        string? selectedNodeId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ClusterSearchResultDto>> SearchClusterAsync(
        string clusterId,
        string? relativePath,
        string fileName,
        string query,
        bool regex,
        int context,
        CancellationToken cancellationToken);

    Task<Stream> DownloadClusterZipAsync(
        string clusterId,
        string? relativePath,
        string fileName,
        CancellationToken cancellationToken);

    Task<Stream> DownloadClusterFolderZipAsync(
        string clusterId,
        string? relativePath,
        CancellationToken cancellationToken);

    Task<Stream> DownloadClusterConcatLogAsync(
        string clusterId,
        string? relativePath,
        string fileName,
        CancellationToken cancellationToken);

    Task<FileTailDto> TailClusterFileAsync(
        string clusterId,
        string? relativePath,
        string fileName,
        string? selectedNodeId,
        long position,
        int maxLines,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ClusterFolderSearchResultDto>> SearchClusterFolderAsync(
        string clusterId,
        string? relativePath,
        string query,
        bool regex,
        CancellationToken cancellationToken);

    // ── Local Node Endpoints for Cross-Node Peer Communication ─────────────
    Task<ClusterNodeBrowseResult> LocalNodeBrowseAsync(string? relativePath, CancellationToken cancellationToken);
    Task<ClusterNodeContentResult> LocalNodeContentAsync(string? relativePath, string fileName, CancellationToken cancellationToken);
    Task<List<ClusterSearchResultDto>> LocalNodeSearchAsync(string? relativePath, string fileName, string query, bool regex, int context, CancellationToken cancellationToken);
    Task<List<ClusterFolderSearchResultDto>> LocalNodeSearchFolderAsync(string? relativePath, string query, bool regex, CancellationToken cancellationToken);
    Task<Stream?> LocalNodeDownloadAsync(string? relativePath, string fileName, CancellationToken cancellationToken);
    Task<Stream?> LocalNodeDownloadFolderAsync(string? relativePath, CancellationToken cancellationToken);
}
