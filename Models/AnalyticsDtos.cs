namespace MyLog.Models;

public sealed record LogAnalyticsDto(
    long TotalLines,
    long ErrorCount,
    long WarningCount,
    long InfoCount,
    IReadOnlyList<HourlyBucketDto> HourlyDistribution,
    IReadOnlyList<TopErrorDto> TopErrors
);

public sealed record HourlyBucketDto(
    string HourLabel,
    string RawTimestamp,
    int Errors,
    int Warnings,
    int Infos
);

public sealed record TopErrorDto(
    string Pattern,
    string SearchQuery,
    int Count
);