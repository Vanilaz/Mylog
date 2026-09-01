namespace MyLog.Models;

public sealed record FolderItemDto(string Name, string Path);

public sealed record FileItemDto(string Name, string Path, long Size, DateTime LastModified, string Extension);

public sealed record FileContentDto(string FileName, long Size, DateTime LastModified, string Content, bool IsTruncated);

public sealed record FileListDto(IReadOnlyList<FolderItemDto> Folders, IReadOnlyList<FileItemDto> Files, string CurrentPath, string? ParentPath);

public sealed record FileSearchResultDto(int LineNumber, string Line, bool IsMatch = true);