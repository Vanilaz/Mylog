using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MyLog.Controllers;
using MyLog.Models;
using MyLog.Options;
using MyLog.Services;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace MyLog.Tests;

public class ClusterDownloadAndConcatTests
{
    private readonly Mock<IClusterLogService> _clusterServiceMock;
    private readonly Mock<ILogger<ClusterLogController>> _loggerMock;
    private readonly ClusterLogController _controller;

    public ClusterDownloadAndConcatTests()
    {
        _clusterServiceMock = new Mock<IClusterLogService>();
        _loggerMock = new Mock<ILogger<ClusterLogController>>();
        _controller = new ClusterLogController(_clusterServiceMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task DownloadFolder_ReturnsZipFile()
    {
        var stream = new MemoryStream(new byte[] { 1, 2, 3 });
        _clusterServiceMock.Setup(s => s.DownloadClusterFolderZipAsync("c1", "logs", It.IsAny<CancellationToken>()))
            .ReturnsAsync(stream);

        var result = await _controller.DownloadFolder("c1", "logs", CancellationToken.None);

        var fileResult = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/zip", fileResult.ContentType);
        Assert.Contains("c1", fileResult.FileDownloadName);
    }

    [Fact]
    public async Task DownloadFile_FormatLog_ReturnsPlainTextLog()
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("Node log content"));
        _clusterServiceMock.Setup(s => s.DownloadClusterConcatLogAsync("c1", "logs", "app.log", It.IsAny<CancellationToken>()))
            .ReturnsAsync(stream);

        var result = await _controller.DownloadFile("c1", "logs", "app.log", "log", CancellationToken.None);

        var fileResult = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("text/plain", fileResult.ContentType);
        Assert.Equal("cluster-c1-app.log", fileResult.FileDownloadName);
    }

    [Fact]
    public async Task DownloadFile_FormatZip_ReturnsZipFile()
    {
        var stream = new MemoryStream(new byte[] { 1, 2, 3 });
        _clusterServiceMock.Setup(s => s.DownloadClusterZipAsync("c1", "logs", "app.log", It.IsAny<CancellationToken>()))
            .ReturnsAsync(stream);

        var result = await _controller.DownloadFile("c1", "logs", "app.log", "zip", CancellationToken.None);

        var fileResult = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/zip", fileResult.ContentType);
        Assert.Equal("cluster-c1-app.zip", fileResult.FileDownloadName);
    }

    [Fact]
    public async Task DownloadFile_MissingFileName_ReturnsBadRequest()
    {
        var result = await _controller.DownloadFile("c1", "logs", "", null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task NodeDownloadFolder_ReturnsZip()
    {
        var stream = new MemoryStream(new byte[] { 1, 2, 3 });
        _clusterServiceMock.Setup(s => s.LocalNodeDownloadFolderAsync("logs", It.IsAny<CancellationToken>()))
            .ReturnsAsync(stream);

        var result = await _controller.NodeDownloadFolder("logs", CancellationToken.None);

        var fileResult = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/zip", fileResult.ContentType);
    }

    [Fact]
    public async Task NodeDownloadFolder_NotFound_ReturnsNotFound()
    {
        _clusterServiceMock.Setup(s => s.LocalNodeDownloadFolderAsync("nonexistent", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream?)null);

        var result = await _controller.NodeDownloadFolder("nonexistent", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }
}

public class ClusterLogServiceConcatAndDownloadTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _node1Dir;
    private readonly string _node2Dir;

    public ClusterLogServiceConcatAndDownloadTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "mylog_concat_test_" + Guid.NewGuid().ToString("N"));
        _node1Dir = Path.Combine(_testRoot, "node1");
        _node2Dir = Path.Combine(_testRoot, "node2");
        Directory.CreateDirectory(_node1Dir);
        Directory.CreateDirectory(_node2Dir);

        // Node 1 logs: line 1 at 12:00:00, line 2 at 12:02:00
        File.WriteAllText(Path.Combine(_node1Dir, "app.log"),
            "2026-09-04 12:00:00 [INFO] Node 1 line 1\n2026-09-04 12:02:00 [INFO] Node 1 line 2\n");

        // Node 2 logs: line 1 at 12:01:00
        File.WriteAllText(Path.Combine(_node2Dir, "app.log"),
            "2026-09-04 12:01:00 [INFO] Node 2 line 1\n");
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
    public async Task ReadClusterFile_TimelineMode_InterleavesChronologically()
    {
        var service = CreateService();

        var result = await service.ReadClusterFileAsync("cluster-test", "", "app.log", "all", "timeline", CancellationToken.None);

        Assert.Equal(3, result.Lines.Count);
        Assert.Equal("timeline", result.ViewMode);
        // Order by timestamp: 12:00:00 -> 12:01:00 -> 12:02:00
        Assert.Contains("Node 1 line 1", result.Lines[0].Line);
        Assert.Contains("Node 2 line 1", result.Lines[1].Line);
        Assert.Contains("Node 1 line 2", result.Lines[2].Line);
    }

    [Fact]
    public async Task ReadClusterFile_ConcatMode_GroupsSequentiallyByNode()
    {
        var service = CreateService();

        var result = await service.ReadClusterFileAsync("cluster-test", "", "app.log", "all", "concat", CancellationToken.None);

        Assert.Equal(3, result.Lines.Count);
        Assert.Equal("concat", result.ViewMode);
        // Sequential by node: Node 1 all lines (line 1, line 2), then Node 2 line 1
        Assert.Contains("Node 1 line 1", result.Lines[0].Line);
        Assert.Contains("Node 1 line 2", result.Lines[1].Line);
        Assert.Contains("Node 2 line 1", result.Lines[2].Line);
    }

    [Fact]
    public async Task DownloadClusterConcatLog_ProducesStructuredPlainText()
    {
        var service = CreateService();

        using var stream = await service.DownloadClusterConcatLogAsync("cluster-test", "", "app.log", CancellationToken.None);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync();

        Assert.Contains("=== Node: Node 1", text);
        Assert.Contains("=== Node: Node 2", text);
        Assert.Contains("Node 1 line 1", text);
        Assert.Contains("Node 2 line 1", text);
    }

    [Fact]
    public async Task DownloadClusterFolderZip_CreatesZipWithNodeFolders()
    {
        var service = CreateService();

        using var stream = await service.DownloadClusterFolderZipAsync("cluster-test", "", CancellationToken.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var entryNames = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Contains(entryNames, name => name.StartsWith("Node_1/") || name.StartsWith("Node 1/"));
        Assert.Contains(entryNames, name => name.StartsWith("Node_2/") || name.StartsWith("Node 2/"));
    }

    [Fact]
    public async Task ReadClusterFile_ConcatMode_PreservesExactOrder_AndDoesNotSwap()
    {
        var service = CreateService();

        var node1Path = Path.Combine(_node1Dir, "ordered.log");
        var node2Path = Path.Combine(_node2Dir, "ordered.log");

        await File.WriteAllLinesAsync(node1Path, new[]
        {
            "2026-09-04 10:00:00 [INFO] Node 1 - Step 1",
            "2026-09-04 10:00:01 [INFO] Node 1 - Step 2",
            "2026-09-04 10:00:02 [INFO] Node 1 - Step 3"
        });

        await File.WriteAllLinesAsync(node2Path, new[]
        {
            "2026-09-04 09:00:00 [INFO] Node 2 - Step 1",
            "2026-09-04 09:00:01 [INFO] Node 2 - Step 2"
        });

        var result = await service.ReadClusterFileAsync("cluster-test", "", "ordered.log", "all", "concat", CancellationToken.None);

        Assert.Equal(5, result.Lines.Count);
        // Node 1 lines must come first in ascending order
        Assert.Equal("node-1", result.Lines[0].NodeId);
        Assert.Contains("Node 1 - Step 1", result.Lines[0].Line);
        Assert.Equal("node-1", result.Lines[1].NodeId);
        Assert.Contains("Node 1 - Step 2", result.Lines[1].Line);
        Assert.Equal("node-1", result.Lines[2].NodeId);
        Assert.Contains("Node 1 - Step 3", result.Lines[2].Line);

        // Node 2 lines must follow in ascending order, NEVER swapped with Node 1
        Assert.Equal("node-2", result.Lines[3].NodeId);
        Assert.Contains("Node 2 - Step 1", result.Lines[3].Line);
        Assert.Equal("node-2", result.Lines[4].NodeId);
        Assert.Contains("Node 2 - Step 2", result.Lines[4].Line);
    }

    [Fact]
    public async Task ReadClusterFile_ConcatMode_ManyLines_KeepsBothNodesInCorrectOrder()
    {
        var service = CreateService();

        var node1Path = Path.Combine(_node1Dir, "large.log");
        var node2Path = Path.Combine(_node2Dir, "large.log");

        var node1Lines = Enumerable.Range(1, 200).Select(i => $"2026-09-04 10:00:00 [INFO] Node 1 Entry {i:D4}");
        var node2Lines = Enumerable.Range(1, 200).Select(i => $"2026-09-04 11:00:00 [INFO] Node 2 Entry {i:D4}");

        await File.WriteAllLinesAsync(node1Path, node1Lines);
        await File.WriteAllLinesAsync(node2Path, node2Lines);

        var result = await service.ReadClusterFileAsync("cluster-test", "", "large.log", "all", "concat", CancellationToken.None);

        Assert.Equal(400, result.Lines.Count);
        // All Node 1 lines first
        for (int i = 0; i < 200; i++)
        {
            Assert.Equal("node-1", result.Lines[i].NodeId);
            Assert.Contains($"Node 1 Entry {i + 1:D4}", result.Lines[i].Line);
        }
        // All Node 2 lines second
        for (int i = 200; i < 400; i++)
        {
            Assert.Equal("node-2", result.Lines[i].NodeId);
            Assert.Contains($"Node 2 Entry {i - 199:D4}", result.Lines[i].Line);
        }
    }
}
