namespace MyLog.Models;

public sealed record ClusterNodeConfig(string NodeId, string NodeName, string Path, string? ApiUrl = null, string? HostHeader = null);

public sealed record ClusterConfig(string ClusterId, string ClusterName, string? Description, List<ClusterNodeConfig> Nodes);

public sealed record ClusterNodeDto(string NodeId, string NodeName, bool IsReachable, string? StatusMessage = null);

public sealed record ClusterInfoDto(string ClusterId, string ClusterName, string? Description, IReadOnlyList<ClusterNodeDto> Nodes);

public sealed record ClusterFileItemDto(
    string Name,
    string RelativePath,
    long MaxSize,
    DateTime LastModified,
    IReadOnlyList<string> AvailableNodeIds,
    IReadOnlyList<string> AvailableNodeNames,
    string Extension = "");

public sealed record ClusterFolderItemDto(
    string Name,
    string RelativePath);

public sealed record ClusterFileListDto(
    string ClusterId,
    string CurrentPath,
    string? ParentPath,
    IReadOnlyList<ClusterFolderItemDto> Folders,
    IReadOnlyList<ClusterFileItemDto> Files,
    IReadOnlyList<ClusterNodeDto> Nodes);

public sealed record ClusterFileLineDto(
    int LineNumber,
    string Line,
    string NodeId,
    string NodeName,
    DateTime? Timestamp = null);

public sealed record ClusterFileContentDto(
    string ClusterId,
    string FileName,
    string? SelectedNodeId,
    bool IsMergedView,
    IReadOnlyList<ClusterNodeDto> Nodes,
    IReadOnlyList<ClusterFileLineDto> Lines,
    bool IsTruncated,
    int TotalLines,
    string ViewMode = "timeline");

public sealed record ClusterSearchResultDto(
    int LineNumber,
    string Line,
    string NodeId,
    string NodeName,
    bool IsMatch = true);

public sealed record ClusterNodeBrowseResult(
    List<ClusterFolderItemDto> Folders,
    List<FileItemDto> Files);

public sealed record ClusterNodeContentResult(
    List<ClusterFileLineDto> Lines,
    bool IsTruncated,
    int TotalLines);

public sealed record ClusterFolderSearchResultDto(
    string FileName,
    string Path,
    int LineNumber,
    string Line,
    string NodeId,
    string NodeName);

