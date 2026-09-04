namespace MyLog.Options;

public class RemoteLogOptions
{
    public bool UseUatEnvironment { get; set; } = false;

    public string SitBaseUrl { get; set; } = string.Empty;
    public string SitApiKey { get; set; } = string.Empty;

    public string UatBaseUrl { get; set; } = string.Empty;
    public string UatApiKey { get; set; } = string.Empty;

    public string BaseUrl => UseUatEnvironment ? UatBaseUrl : SitBaseUrl;
    public string ApiKey => UseUatEnvironment ? UatApiKey : SitApiKey;

    public string ListProjectsPath { get; set; } = string.Empty;
    public string ListFilesPath { get; set; } = string.Empty;
    public string BrowseFilePath { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 120;
    public int MaxZipSizeMB { get; set; } = 100;
    public int MaxExtractedSizeMB { get; set; } = 300;
    public string Cookie { get; set; } = string.Empty;
}
