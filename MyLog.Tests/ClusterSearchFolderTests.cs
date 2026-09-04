using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MyLog.Controllers;
using MyLog.Models;
using MyLog.Options;
using MyLog.Services;
using Xunit;

namespace MyLog.Tests;

public class ClusterSearchFolderTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _node1Dir;
    private readonly string _node2Dir;

    public ClusterSearchFolderTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "mylog_cluster_search_" + Guid.NewGuid().ToString("N"));
        _node1Dir = Path.Combine(_testRoot, "node1");
        _node2Dir = Path.Combine(_testRoot, "node2");
        Directory.CreateDirectory(_node1Dir);
        Directory.CreateDirectory(_node2Dir);

        File.WriteAllText(Path.Combine(_node1Dir, "service-a.log"),
            "2026-09-04 10:00:00 [INFO] Starting service A\n2026-09-04 10:01:00 [ERROR] Connection timeout for RequestId-999\n");

        File.WriteAllText(Path.Combine(_node2Dir, "service-b.log"),
            "2026-09-04 10:02:00 [INFO] Node 2 healthy\n2026-09-04 10:03:00 [WARN] Retry attempt for RequestId-999\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_testRoot, true); } catch { }
    }

    private ClusterLogService CreateService()
    {
        var clusterOptions = Microsoft.Extensions.Options.Options.Create(new ClusterOptions
        {
            Clusters = [
                new ClusterConfig(
                    "cluster-test",
                    "Cluster Test",
                    "Description",
                    [
                        new ClusterNodeConfig("node-1", "Node 1", _node1Dir),
                        new ClusterNodeConfig("node-2", "Node 2", _node2Dir)
                    ]
                )
            ]
        });

        var browserOptions = Microsoft.Extensions.Options.Options.Create(new FileBrowserOptions
        {
            AllowedFolders = [_node1Dir, _node2Dir]
        });

        var loggerMock = new Mock<ILogger<ClusterLogService>>();
        var clientFactoryMock = new Mock<IHttpClientFactory>();
        var envMock = new Mock<Microsoft.Extensions.Hosting.IHostEnvironment>();
        var analyticsMock = new Mock<ILogAnalyticsService>();
        var fileBrowserMock = new Mock<IFileBrowserService>();

        return new ClusterLogService(
            loggerMock.Object,
            clusterOptions,
            browserOptions,
            clientFactoryMock.Object,
            envMock.Object,
            analyticsMock.Object,
            fileBrowserMock.Object);
    }

    [Fact]
    public async Task SearchClusterFolder_FindsMatchesAcrossAllNodes()
    {
        var service = CreateService();

        var results = await service.SearchClusterFolderAsync("cluster-test", "", "RequestId-999", false, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.NodeId == "node-1" && r.FileName == "service-a.log" && r.Line.Contains("Connection timeout"));
        Assert.Contains(results, r => r.NodeId == "node-2" && r.FileName == "service-b.log" && r.Line.Contains("Retry attempt"));
    }

    [Fact]
    public async Task SearchClusterFolder_ControllerEndpoint_ReturnsOkResult()
    {
        var clusterServiceMock = new Mock<IClusterLogService>();
        var loggerMock = new Mock<ILogger<ClusterLogController>>();

        clusterServiceMock.Setup(s => s.SearchClusterFolderAsync("c1", "logs", "test-query", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ClusterFolderSearchResultDto>
            {
                new("app.log", "logs/app.log", 10, "Found test-query here", "n1", "Node 1")
            });

        var controller = new ClusterLogController(clusterServiceMock.Object, loggerMock.Object);

        var result = await controller.SearchFolder("c1", "logs", "test-query", false, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var list = Assert.IsAssignableFrom<IReadOnlyList<ClusterFolderSearchResultDto>>(okResult.Value);
        Assert.Single(list);
        Assert.Equal("Node 1", list[0].NodeName);
    }

    [Fact]
    public async Task SearchClusterFolder_EmptyQuery_ReturnsBadRequest()
    {
        var clusterServiceMock = new Mock<IClusterLogService>();
        var loggerMock = new Mock<ILogger<ClusterLogController>>();
        var controller = new ClusterLogController(clusterServiceMock.Object, loggerMock.Object);

        var result = await controller.SearchFolder("c1", "logs", "", false, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
