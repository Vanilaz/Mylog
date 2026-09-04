using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using MyLog.Models;
using MyLog.Options;
using MyLog.Services;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Xunit;

namespace MyLog.Tests;

public class RemoteLogServiceTests
{
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private readonly HttpClient _httpClient;
    private readonly Mock<ILogger<RemoteLogService>> _loggerMock;
    private readonly Mock<ILogAnalyticsService> _analyticsMock;
    private readonly IOptions<RemoteLogOptions> _options;

    public RemoteLogServiceTests()
    {
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_httpMessageHandlerMock.Object)
        {
            BaseAddress = new Uri("https://gateway/")
        };

        _loggerMock = new Mock<ILogger<RemoteLogService>>();
        _analyticsMock = new Mock<ILogAnalyticsService>();
        
        var options = new RemoteLogOptions
        {
            SitBaseUrl = "https://gateway/",
            ListProjectsPath = "api/projects",
            ListFilesPath = "api/files",
            BrowseFilePath = "api/browse",
            MaxExtractedSizeMB = 10,
            MaxZipSizeMB = 10
        };
        _options = Microsoft.Extensions.Options.Options.Create(options);
    }

    [Fact]
    public async Task GetProjectsAsync_Success_ReturnsProjects()
    {
        // Arrange
        var json = "[\"ProjectA\", \"ProjectB\"]";
        SetupMockResponse(HttpStatusCode.OK, json);
        var service = new RemoteLogService(_httpClient, _options, _loggerMock.Object, _analyticsMock.Object);

        // Act
        var result = await service.GetProjectsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("ProjectA", result[0].Name);
    }

    [Fact]
    public async Task GetProjectsAsync_EmptyResponse_ReturnsEmpty()
    {
        SetupMockResponse(HttpStatusCode.OK, "");
        var service = new RemoteLogService(_httpClient, _options, _loggerMock.Object, _analyticsMock.Object);
        var result = await service.GetProjectsAsync(CancellationToken.None);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetProjectsAsync_InvalidJson_ThrowsInvalidOperation()
    {
        SetupMockResponse(HttpStatusCode.OK, "invalid");
        var service = new RemoteLogService(_httpClient, _options, _loggerMock.Object, _analyticsMock.Object);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetProjectsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetFilesAsync_Success_ReturnsFiles()
    {
        var json = "[\"file1.log\", \"file2.txt\"]";
        SetupMockResponse(HttpStatusCode.OK, json);
        var service = new RemoteLogService(_httpClient, _options, _loggerMock.Object, _analyticsMock.Object);
        
        var result = await service.GetFilesAsync("ProjectA", CancellationToken.None);
        
        Assert.Equal(2, result.Files.Count);
        Assert.Equal("file1.log", result.Files[0].Name);
        Assert.Equal(".log", result.Files[0].Extension);
    }

    [Fact]
    public async Task GetFilesAsync_WithSubfolders_ClassifiesCorrectly()
    {
        var json = "[\"client\", \"service\", \"readme.txt\"]";
        SetupMockResponse(HttpStatusCode.OK, json);
        var service = new RemoteLogService(_httpClient, _options, _loggerMock.Object, _analyticsMock.Object);

        var result = await service.GetFilesAsync("HPCollectionWS", CancellationToken.None);

        Assert.Equal(2, result.Folders.Count);
        Assert.Equal("client", result.Folders[0].Name);
        Assert.Equal("HPCollectionWS/client", result.Folders[0].Path);
        Assert.Equal("service", result.Folders[1].Name);
        Assert.Equal("HPCollectionWS/service", result.Folders[1].Path);

        Assert.Single(result.Files);
        Assert.Equal("readme.txt", result.Files[0].Name);
    }

    [Fact]
    public async Task GetFilesAsync_ProjectNameEmpty_ThrowsArgumentException()
    {
        var service = new RemoteLogService(_httpClient, _options, _loggerMock.Object, _analyticsMock.Object);
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetFilesAsync("", CancellationToken.None));
    }
    
    [Fact]
    public async Task BrowseFileAsync_Success_ReturnsContent()
    {
        RemoteLogService.ClearZipCache();
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry("test.log");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("Hello Log");
        }
        ms.Position = 0;
        
        SetupMockResponse(HttpStatusCode.OK, ms.ToArray());
        var service = new RemoteLogService(_httpClient, _options, _loggerMock.Object, _analyticsMock.Object);

        var result = await service.BrowseFileAsync("Proj_Browse", "test.log", CancellationToken.None);

        Assert.Equal("test.log", result.FileName);
        Assert.Equal("Hello Log", result.Content);
        Assert.False(result.IsTruncated);
    }
    
    [Fact]
    public async Task BrowseFileAsync_EmptyZip_ThrowsFileNotFound()
    {
        RemoteLogService.ClearZipCache();
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            // Empty archive
        }
        ms.Position = 0;
        
        SetupMockResponse(HttpStatusCode.OK, ms.ToArray());
        var service = new RemoteLogService(_httpClient, _options, _loggerMock.Object, _analyticsMock.Object);

        await Assert.ThrowsAsync<FileNotFoundException>(() => service.BrowseFileAsync("Proj_Empty", "test.log", CancellationToken.None));
    }

    [Fact]
    public async Task BrowseFileAsync_InvalidZip_ThrowsInvalidOperation()
    {
        RemoteLogService.ClearZipCache();
        SetupMockResponse(HttpStatusCode.OK, new byte[] { 1, 2, 3 });
        var service = new RemoteLogService(_httpClient, _options, _loggerMock.Object, _analyticsMock.Object);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.BrowseFileAsync("Proj_Invalid", "test.log", CancellationToken.None));
    }
    
    [Fact]
    public async Task SearchContentAsync_Success_ReturnsMatches()
    {
        RemoteLogService.ClearZipCache();
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry("test.log");
            using var writer = new StreamWriter(entry.Open());
            writer.WriteLine("Line 1");
            writer.WriteLine("Error Line");
            writer.WriteLine("Line 3");
        }
        ms.Position = 0;
        
        SetupMockResponse(HttpStatusCode.OK, ms.ToArray());
        var service = new RemoteLogService(_httpClient, _options, _loggerMock.Object, _analyticsMock.Object);

        var result = await service.SearchContentAsync("Proj_Search", "test.log", "Error", false, 0, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(2, result[0].LineNumber);
        Assert.Equal("Error Line", result[0].Line);
        Assert.True(result[0].IsMatch);
    }
    
    [Fact]
    public async Task SearchContentAsync_Regex_ReturnsMatches()
    {
        RemoteLogService.ClearZipCache();
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry("test.log");
            using var writer = new StreamWriter(entry.Open());
            writer.WriteLine("Line 1");
            writer.WriteLine("Error Line 404");
            writer.WriteLine("Line 3");
        }
        ms.Position = 0;
        
        SetupMockResponse(HttpStatusCode.OK, ms.ToArray());
        var service = new RemoteLogService(_httpClient, _options, _loggerMock.Object, _analyticsMock.Object);

        var result = await service.SearchContentAsync("Proj_Regex", "test.log", @"\d{3}", true, 0, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(2, result[0].LineNumber);
        Assert.True(result[0].IsMatch);
    }
    
    [Fact]
    public async Task AnalyzeRemoteFileAsync_Success_ReturnsAnalytics()
    {
        RemoteLogService.ClearZipCache();
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry("app.log");
            using var entryStream = entry.Open();
            using var writer = new StreamWriter(entryStream);
            writer.WriteLine("2026-01-01 10:00:00 INFO Started");
            writer.WriteLine("2026-01-01 10:01:00 ERROR Failed");
        }
        ms.Position = 0;

        SetupMockResponse(HttpStatusCode.OK, ms.ToArray());

        var expectedAnalytics = new LogAnalyticsDto(2, 1, 0, 1, Array.Empty<HourlyBucketDto>(), Array.Empty<TopErrorDto>());
        _analyticsMock.Setup(m => m.AnalyzeLines(It.IsAny<IEnumerable<string>>())).Returns(expectedAnalytics);

        var service = new RemoteLogService(_httpClient, _options, _loggerMock.Object, _analyticsMock.Object);

        var result = await service.AnalyzeRemoteFileAsync("Proj_Analyze", "app.log", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result.TotalLines);
        Assert.Equal(1, result.ErrorCount);
    }

    [Fact]
    public async Task BrowseThenSearch_WithinCacheDuration_CallsGatewayOnlyOnce()
    {
        RemoteLogService.ClearZipCache();
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry("test.log");
            using var writer = new StreamWriter(entry.Open());
            writer.WriteLine("Line 1: system started");
            writer.WriteLine("Line 2: error occurred in module");
        }
        ms.Position = 0;

        SetupMockResponse(HttpStatusCode.OK, ms.ToArray());
        var service = new RemoteLogService(_httpClient, _options, _loggerMock.Object, _analyticsMock.Object);

        // 1st call: browse file -> should download ZIP from gateway
        var browseResult = await service.BrowseFileAsync("Proj_Cache", "test.log", CancellationToken.None);
        Assert.Contains("error occurred", browseResult.Content);

        // 2nd call: search file -> should use cached ZIP without extra gateway call
        var searchResult = await service.SearchContentAsync("Proj_Cache", "test.log", "error", false, 0, CancellationToken.None);
        Assert.Single(searchResult);

        // Verify Gateway SendAsync was only called once
        _httpMessageHandlerMock.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("api/browse")),
            ItExpr.IsAny<CancellationToken>()
        );
    }

    [Fact]
    public async Task ServerError_ThrowsInvalidOperation()
    {
        SetupMockResponse(HttpStatusCode.InternalServerError, "");
        var service = new RemoteLogService(_httpClient, _options, _loggerMock.Object, _analyticsMock.Object);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetProjectsAsync(CancellationToken.None));
    }

    private void SetupMockResponse(HttpStatusCode statusCode, string content)
    {
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(content)
            });
    }

    private void SetupMockResponse(HttpStatusCode statusCode, byte[] content)
    {
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new ByteArrayContent(content)
            });
    }
}
