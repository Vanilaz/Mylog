using Microsoft.AspNetCore.Mvc;
using Moq;
using MyLog.Controllers;
using MyLog.Models;
using MyLog.Services;
using System.Net;
using Xunit;

namespace MyLog.Tests;

public class RemoteLogControllerTests
{
    private readonly Mock<IRemoteLogService> _remoteLogServiceMock;
    private readonly RemoteLogController _controller;

    public RemoteLogControllerTests()
    {
        _remoteLogServiceMock = new Mock<IRemoteLogService>();
        _controller = new RemoteLogController(_remoteLogServiceMock.Object);
    }

    [Fact]
    public async Task GetProjects_ReturnsOk()
    {
        _remoteLogServiceMock.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new FolderItemDto("ProjA", "ProjA") });

        var result = await _controller.GetProjects(CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = Assert.IsAssignableFrom<IReadOnlyList<FolderItemDto>>(okResult.Value);
        Assert.Single(value);
    }

    [Fact]
    public async Task GetProjects_Error_ReturnsBadRequest()
    {
        _remoteLogServiceMock.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Gateway Error"));

        var result = await _controller.GetProjects(CancellationToken.None);

        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetFiles_ReturnsOk()
    {
        _remoteLogServiceMock.Setup(s => s.GetFilesAsync("Proj", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileListDto(Array.Empty<FolderItemDto>(), new[] { new FileItemDto("a.log", "Proj/a.log", 100, DateTime.UtcNow, ".log") }, "Proj", null));

        var result = await _controller.GetFiles("Proj", CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = Assert.IsType<FileListDto>(okResult.Value);
        Assert.Single(value.Files);
    }

    [Fact]
    public async Task GetContent_ReturnsOk()
    {
        _remoteLogServiceMock.Setup(s => s.BrowseFileAsync("Proj", "a.log", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileContentDto("a.log", 100, DateTime.UtcNow, "content", false));

        var result = await _controller.GetContent("Proj", "a.log", CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = Assert.IsType<FileContentDto>(okResult.Value);
        Assert.Equal("content", value.Content);
    }

    [Fact]
    public async Task Search_ReturnsOk()
    {
        _remoteLogServiceMock.Setup(s => s.SearchContentAsync("Proj", "a.log", "err", false, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new FileSearchResultDto(1, "err line", true) });

        var result = await _controller.Search("Proj", "a.log", "err", false, 0, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = Assert.IsAssignableFrom<IReadOnlyList<FileSearchResultDto>>(okResult.Value);
        Assert.Single(value);
    }

    [Fact]
    public async Task Download_ReturnsFileResult()
    {
        var stream = new MemoryStream(new byte[] { 1, 2, 3 });
        _remoteLogServiceMock.Setup(s => s.DownloadFileAsync("Proj", "a.log", It.IsAny<CancellationToken>()))
            .ReturnsAsync((stream, "a.log"));

        var result = await _controller.Download("Proj", "a.log", CancellationToken.None);

        var fileResult = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/octet-stream", fileResult.ContentType);
        Assert.Equal("a.log", fileResult.FileDownloadName);
    }

    [Fact]
    public async Task Download_MissingParams_ReturnsBadRequest()
    {
        var result = await _controller.Download("", "a.log", CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Download_FileNotFound_ReturnsNotFound()
    {
        _remoteLogServiceMock.Setup(s => s.DownloadFileAsync("Proj", "missing.log", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileNotFoundException("No file inside zip"));

        var result = await _controller.Download("Proj", "missing.log", CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task DownloadFolder_ReturnsZipFileResult()
    {
        var stream = new MemoryStream(new byte[] { 1, 2, 3 });
        _remoteLogServiceMock.Setup(s => s.DownloadProjectZipAsync("Proj", It.IsAny<CancellationToken>()))
            .ReturnsAsync(stream);

        var result = await _controller.DownloadFolder("Proj", CancellationToken.None);

        var fileResult = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/zip", fileResult.ContentType);
        Assert.Contains("Proj", fileResult.FileDownloadName);
    }

    [Fact]
    public async Task DownloadFolder_MissingProject_ReturnsBadRequest()
    {
        var result = await _controller.DownloadFolder("", CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }
}
