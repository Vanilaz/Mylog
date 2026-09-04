using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MyLog.Models;
using MyLog.Options;
using MyLog.Services;
using Xunit;

namespace MyLog.Tests;

public class ClusterFaultToleranceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _localLogFile;
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<IHostEnvironment> _environmentMock;
    private readonly Mock<ILogAnalyticsService> _analyticsMock;
    private readonly Mock<IFileBrowserService> _fileBrowserMock;
    private readonly Mock<ILogger<ClusterLogService>> _loggerMock;

    public ClusterFaultToleranceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"mylog_cluster_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _localLogFile = Path.Combine(_tempDir, "app.log");
        File.WriteAllText(_localLogFile, "2026-09-04 10:00:00 [INFO] Local log entry\n");

        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        // Return a client that immediately fails or times out for unreachable peer
        var fakeHandler = new FakeHttpMessageHandler(new HttpRequestException("Connection refused by dead node"));
        var httpClient = new HttpClient(fakeHandler);
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        _environmentMock = new Mock<IHostEnvironment>();
        _environmentMock.Setup(e => e.ContentRootPath).Returns(_tempDir);
        _analyticsMock = new Mock<ILogAnalyticsService>();
        _fileBrowserMock = new Mock<IFileBrowserService>();
        _loggerMock = new Mock<ILogger<ClusterLogService>>();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Exception _exception;
        public FakeHttpMessageHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromException<HttpResponseMessage>(_exception);
        }
    }

    [Fact]
    public async Task BrowseClusterPathAsync_WhenRemoteNodeIsDown_ReturnsFilesFromReachableNode()
    {
        var clusterOptions = Microsoft.Extensions.Options.Options.Create(new ClusterOptions
        {
            Clusters = [
                new ClusterConfig(
                    "test-cluster",
                    "Test Cluster",
                    "Test Desc",
                    [
                        // Node 1: Local node (reachable)
                        new ClusterNodeConfig("node-1", "Node 1 Local", _tempDir),
                        // Node 2: Remote dead node (fails HTTP with connection refused)
                        new ClusterNodeConfig("node-2", "Node 2 Dead", @"\\10.99.99.99\d$\logs", "https://10.99.99.99/UAT/mylog")
                    ]
                )
            ]
        });

        var browserOptions = Microsoft.Extensions.Options.Options.Create(new FileBrowserOptions
        {
            AllowedFolders = [_tempDir]
        });

        var service = new ClusterLogService(
            _loggerMock.Object,
            clusterOptions,
            browserOptions,
            _httpClientFactoryMock.Object,
            _environmentMock.Object,
            _analyticsMock.Object,
            _fileBrowserMock.Object);

        var result = await service.BrowseClusterPathAsync("test-cluster", "", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result.Nodes.Count);

        // Node 1 should be reachable
        var node1 = result.Nodes.First(n => n.NodeId == "node-1");
        Assert.True(node1.IsReachable);

        // Node 2 should be marked unreachable
        var node2 = result.Nodes.First(n => n.NodeId == "node-2");
        Assert.False(node2.IsReachable);

        // Files from reachable node 1 should still be listed!
        Assert.NotEmpty(result.Files);
        Assert.Contains(result.Files, f => f.Name == "app.log");
    }

    [Fact]
    public async Task ReadClusterFileAsync_WhenRemoteNodeIsDown_ReturnsMergedLinesFromReachableNode()
    {
        var clusterOptions = Microsoft.Extensions.Options.Options.Create(new ClusterOptions
        {
            Clusters = [
                new ClusterConfig(
                    "test-cluster",
                    "Test Cluster",
                    "Test Desc",
                    [
                        new ClusterNodeConfig("node-1", "Node 1 Local", _tempDir),
                        new ClusterNodeConfig("node-2", "Node 2 Dead", @"\\10.99.99.99\d$\logs", "https://10.99.99.99/UAT/mylog")
                    ]
                )
            ]
        });

        var browserOptions = Microsoft.Extensions.Options.Options.Create(new FileBrowserOptions
        {
            AllowedFolders = [_tempDir]
        });

        var service = new ClusterLogService(
            _loggerMock.Object,
            clusterOptions,
            browserOptions,
            _httpClientFactoryMock.Object,
            _environmentMock.Object,
            _analyticsMock.Object,
            _fileBrowserMock.Object);

        var content = await service.ReadClusterFileAsync("test-cluster", "", "app.log", "all", CancellationToken.None);

        Assert.NotNull(content);
        Assert.NotEmpty(content.Lines);
        Assert.Contains(content.Lines, l => l.Line.Contains("Local log entry"));

        // Verify nodes list in content has unreachable status for node 2
        var node2 = content.Nodes.First(n => n.NodeId == "node-2");
        Assert.False(node2.IsReachable);
    }
}