using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MyLog.Options;
using MyLog.Services;
using Xunit;

namespace MyLog.Tests;

public class HttpTailTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tempFile;
    private readonly FileBrowserService _service;

    public HttpTailTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"mylog_httptail_dir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _tempFile = Path.Combine(_tempDir, "test.log");
        File.WriteAllText(_tempFile, "Line 1\nLine 2\nLine 3\n");

        var options = Microsoft.Extensions.Options.Options.Create(new FileBrowserOptions
        {
            AllowedFolders = [_tempDir]
        });

        var envMock = new Mock<IHostEnvironment>();
        envMock.Setup(e => e.ContentRootPath).Returns(_tempDir);
        var loggerMock = new Mock<ILogger<FileBrowserService>>();

        _service = new FileBrowserService(options, envMock.Object, loggerMock.Object);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task GetTailAsync_InitialTail_ReturnsContentAndNewPosition()
    {
        var result = await _service.GetTailAsync(_tempFile, position: 0, maxLines: 100, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("test.log", result.FileName);
        Assert.True(result.NewPosition > 0);
        Assert.True(result.FileSize > 0);
        Assert.True(result.IsReset);
        Assert.Contains("Line 1", result.Content);
        Assert.Contains("Line 3", result.Content);
    }

    [Fact]
    public async Task GetTailAsync_IncrementalRead_ReturnsOnlyNewAppendedText()
    {
        var initial = await _service.GetTailAsync(_tempFile, position: 0, maxLines: 100, CancellationToken.None);
        var initialPos = initial.NewPosition;

        // Append new line
        await File.AppendAllTextAsync(_tempFile, "Line 4 appended\n");

        var incremental = await _service.GetTailAsync(_tempFile, position: initialPos, maxLines: 100, CancellationToken.None);

        Assert.False(incremental.IsReset);
        Assert.True(incremental.NewPosition > initialPos);
        Assert.Contains("Line 4 appended", incremental.Content);
        Assert.DoesNotContain("Line 1", incremental.Content);
    }

    [Fact]
    public async Task GetTailAsync_NoChanges_ReturnsEmptyContent()
    {
        var initial = await _service.GetTailAsync(_tempFile, position: 0, maxLines: 100, CancellationToken.None);
        var pos = initial.NewPosition;

        var followUp = await _service.GetTailAsync(_tempFile, position: pos, maxLines: 100, CancellationToken.None);

        Assert.Equal(pos, followUp.NewPosition);
        Assert.Empty(followUp.Content);
        Assert.Empty(followUp.Lines);
        Assert.False(followUp.IsReset);
    }

    [Fact]
    public async Task GetTailAsync_FileRotated_ResetsAndReturnsNewContent()
    {
        var initial = await _service.GetTailAsync(_tempFile, position: 0, maxLines: 100, CancellationToken.None);
        var oldPos = initial.NewPosition;

        // Truncate / replace file with shorter content
        await File.WriteAllTextAsync(_tempFile, "New log after rotation\n");

        // Request with old position that exceeds new file length
        var afterRotate = await _service.GetTailAsync(_tempFile, position: oldPos + 1000, maxLines: 100, CancellationToken.None);

        Assert.True(afterRotate.IsReset);
        Assert.Contains("New log after rotation", afterRotate.Content);
    }

    [Fact]
    public async Task GetTailAsync_FileNotFound_ThrowsFileNotFoundException()
    {
        var nonexistent = Path.Combine(_tempDir, "nonexistent.log");
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _service.GetTailAsync(nonexistent, 0, 100, CancellationToken.None));
    }
}