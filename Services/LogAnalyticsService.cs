namespace MyLog.Services;

using System.Text.RegularExpressions;
using MyLog.Models;

public sealed partial class LogAnalyticsService : ILogAnalyticsService
{
    private const int MaxLines = 200_000;
    private readonly IFileBrowserService _files;
    private readonly ILogger<LogAnalyticsService> _logger;

    [GeneratedRegex(@"^\[?(\d{4}[-/]\d{1,2}[-/]\d{1,2}[ T]\d{1,2}|\d{1,2}[-/]\d{1,2}[-/]\d{2,4}[ T]\d{1,2})", RegexOptions.Compiled)]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"([A-Za-z0-9_.]*(?:Exception|Error)):?\s*([^\r\n]{1,100})", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ExceptionTypeRegex();

    public LogAnalyticsService(IFileBrowserService files, ILogger<LogAnalyticsService> logger)
    {
        _files = files;
        _logger = logger;
    }

    public async Task<LogAnalyticsDto> AnalyzeAsync(string path, CancellationToken cancellationToken)
    {
        var safePath = _files.GetSafeFilePath(path);
        var fileInfo = new FileInfo(safePath);
        if (!fileInfo.Exists) throw new FileNotFoundException("Log file not found.", path);

        if (ArchiveLogReader.IsArchive(safePath))
        {
            var (archiveLines, _) = await ArchiveLogReader.ReadArchiveLinesAsync(safePath, MaxLines, cancellationToken);
            return AnalyzeLines(archiveLines);
        }

        const long scanLimit = 50L * 1024 * 1024;
        var startOffset = Math.Max(0, fileInfo.Length - scanLimit);

        await using var fileStream = new FileStream(safePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
        if (startOffset > 0)
        {
            fileStream.Seek(startOffset, SeekOrigin.Begin);
        }

        using var reader = new StreamReader(fileStream);
        if (startOffset > 0)
        {
            await reader.ReadLineAsync(cancellationToken);
        }

        var lines = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lines.Add(line);
            if (lines.Count >= MaxLines) break;
        }

        return AnalyzeLines(lines);
    }

    public LogAnalyticsDto AnalyzeLines(IEnumerable<string> lines)
    {
        long totalLines = 0;
        long errorCount = 0;
        long warningCount = 0;
        long infoCount = 0;

        var hourlyBuckets = new Dictionary<string, (string rawTimestamp, int errors, int warns, int infos)>(StringComparer.OrdinalIgnoreCase);
        var topErrors = new Dictionary<string, (string searchQuery, int count)>(StringComparer.OrdinalIgnoreCase);

        string currentHourKey = "Recent";
        string currentRawTs = "";

        foreach (var line in lines)
        {
            totalLines++;

            var (isError, isWarn, isInfo) = ClassifyLogLevel(line);
            if (isError) errorCount++;
            else if (isWarn) warningCount++;
            else if (isInfo) infoCount++;

            UpdateHourlyBucket(line, ref currentHourKey, ref currentRawTs, hourlyBuckets, isError, isWarn, isInfo);

            if (isError)
            {
                RecordTopError(line, topErrors);
            }

            if (totalLines >= MaxLines) break;
        }

        var sortedHourly = hourlyBuckets
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .TakeLast(24)
            .Select(kvp => new HourlyBucketDto(kvp.Key, kvp.Value.rawTimestamp, kvp.Value.errors, kvp.Value.warns, kvp.Value.infos))
            .ToList();

        var sortedTopErrors = topErrors
            .OrderByDescending(kvp => kvp.Value.count)
            .Take(5)
            .Select(kvp => new TopErrorDto(kvp.Key, kvp.Value.searchQuery, kvp.Value.count))
            .ToList();

        return new LogAnalyticsDto(
            totalLines,
            errorCount,
            warningCount,
            infoCount,
            sortedHourly,
            sortedTopErrors
        );
    }

    private static (bool IsError, bool IsWarn, bool IsInfo) ClassifyLogLevel(string line)
    {
        var isError = line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase)
                   || line.Contains("ERROR ", StringComparison.OrdinalIgnoreCase)
                   || line.Contains("EXCEPTION", StringComparison.OrdinalIgnoreCase)
                   || line.Contains("FATAL", StringComparison.OrdinalIgnoreCase)
                   || line.Contains("CRITICAL", StringComparison.OrdinalIgnoreCase);

        var isWarn = !isError && (
            line.Contains("[WARN]", StringComparison.OrdinalIgnoreCase)
         || line.Contains("[WARNING]", StringComparison.OrdinalIgnoreCase)
         || line.Contains("WARN ", StringComparison.OrdinalIgnoreCase)
         || line.Contains("WARNING ", StringComparison.OrdinalIgnoreCase));

        var isInfo = !isError && !isWarn && (
            line.Contains("[INFO]", StringComparison.OrdinalIgnoreCase)
         || line.Contains("INFO ", StringComparison.OrdinalIgnoreCase));

        return (isError, isWarn, isInfo);
    }

    private static void UpdateHourlyBucket(
        string line,
        ref string currentHourKey,
        ref string currentRawTs,
        Dictionary<string, (string rawTimestamp, int errors, int warns, int infos)> hourlyBuckets,
        bool isError,
        bool isWarn,
        bool isInfo)
    {
        var tsMatch = TimestampRegex().Match(line);
        if (tsMatch.Success)
        {
            currentRawTs = tsMatch.Groups[1].Value;
            currentHourKey = currentRawTs.Replace('T', ' ');
        }

        if (!hourlyBuckets.TryGetValue(currentHourKey, out var counts))
        {
            counts = (currentRawTs, 0, 0, 0);
        }

        if (isError) counts.errors++;
        else if (isWarn) counts.warns++;
        else if (isInfo) counts.infos++;

        hourlyBuckets[currentHourKey] = counts;
    }

    private static void RecordTopError(string line, Dictionary<string, (string searchQuery, int count)> topErrors)
    {
        var (displayKey, searchKey) = ExtractErrorKeys(line);
        if (string.IsNullOrWhiteSpace(displayKey)) return;

        if (topErrors.TryGetValue(displayKey, out var val))
        {
            topErrors[displayKey] = (val.searchQuery, val.count + 1);
        }
        else
        {
            topErrors[displayKey] = (searchKey, 1);
        }
    }

    private static (string DisplayKey, string SearchKey) ExtractErrorKeys(string line)
    {
        var exMatch = ExceptionTypeRegex().Match(line);
        if (exMatch.Success)
        {
            var typeName = exMatch.Groups[1].Value.Trim();
            var msgSnippet = exMatch.Groups[2].Value.Trim();
            var maskedMsg = Regex.Replace(msgSnippet, @"\b[0-9a-fA-F-]{8,36}\b|\b\d+\b", "{id}");
            if (maskedMsg.Length > 45) maskedMsg = maskedMsg[..45] + "...";
            return ($"{typeName}: {maskedMsg}", typeName);
        }

        var clean = Regex.Replace(line, @"^[^\]]+\]\s*", "").Trim();
        var words = clean.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        string searchKey = words.Length > 0 ? words[0] : "ERROR";
        string displayKey = clean.Length > 50 ? clean[..50] + "..." : clean;

        return (displayKey, searchKey);
    }
}