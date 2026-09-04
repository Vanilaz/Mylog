using MyLog.Models;

namespace MyLog.Services;

public interface IRemoteLogService
{
    Task<IReadOnlyList<FolderItemDto>> GetProjectsAsync(CancellationToken cancellationToken);
    Task<FileListDto> GetFilesAsync(string projectName, CancellationToken cancellationToken);
    Task<FileContentDto> BrowseFileAsync(string projectName, string fileName, CancellationToken cancellationToken);
    Task<IReadOnlyList<FileSearchResultDto>> SearchContentAsync(string projectName, string fileName, string query, bool regex, int context, CancellationToken cancellationToken);
    Task<LogAnalyticsDto> AnalyzeRemoteFileAsync(string projectName, string fileName, CancellationToken cancellationToken);
    Task<(Stream stream, string resolvedFileName)> DownloadFileAsync(string projectName, string fileName, CancellationToken cancellationToken);
    Task<Stream> DownloadProjectZipAsync(string projectName, CancellationToken cancellationToken);
}
