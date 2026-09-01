using MyLog.Models;

namespace MyLog.Services;

public interface IFileBrowserService
{
    IReadOnlyList<string> AllowedRoots { get; }
    Task<IReadOnlyList<FolderItemDto>> GetRootsAsync(CancellationToken cancellationToken);
    Task<FileListDto> ListAsync(string? path, CancellationToken cancellationToken);
    Task<FileContentDto> ReadContentAsync(string path, CancellationToken cancellationToken);
    Task<IReadOnlyList<FileSearchResultDto>> SearchContentAsync(string path, string query, bool regularExpression, int context, CancellationToken cancellationToken);
    string GetSafeFilePath(string path);
    string GetContentType(string path);
}
