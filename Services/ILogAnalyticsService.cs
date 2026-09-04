namespace MyLog.Services;

using MyLog.Models;

public interface ILogAnalyticsService
{
    Task<LogAnalyticsDto> AnalyzeAsync(string path, CancellationToken cancellationToken);
    LogAnalyticsDto AnalyzeLines(IEnumerable<string> lines);
}