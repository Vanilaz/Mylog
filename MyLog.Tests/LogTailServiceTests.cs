using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using MyLog.Hubs;
using MyLog.Services;
using Xunit;

namespace MyLog.Tests;

public class LogTailServiceTests : IDisposable
{
    private readonly string _tempFile;
    private readonly Mock<IFileBrowserService> _filesMock;
    private readonly Mock<IHubContext<LogHub>> _hubMock;
    private readonly Mock<IHubClients> _clientsMock;
    private readonly Mock<ISingleClientProxy> _clientProxyMock;
    private readonly Mock<IGroupManager> _groupsMock;
    private readonly Mock<ILogger<LogTailService>> _loggerMock;

    public LogTailServiceTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"mylog_tail_test_{Guid.NewGuid():N}.log");
        File.WriteAllText(_tempFile, "Line 1: system initialized\nLine 2: ready\n");

        _filesMock = new Mock<IFileBrowserService>();
        _filesMock.Setup(f => f.GetSafeFilePath(It.IsAny<string>())).Returns(_tempFile);

        _hubMock = new Mock<IHubContext<LogHub>>();
        _clientsMock = new Mock<IHubClients>();
        _clientProxyMock = new Mock<ISingleClientProxy>();
        _groupsMock = new Mock<IGroupManager>();

        _clientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(_clientProxyMock.Object);
        _clientsMock.Setup(c => c.Client(It.IsAny<string>())).Returns(_clientProxyMock.Object);

        _hubMock.Setup(h => h.Clients).Returns(_clientsMock.Object);
        _hubMock.Setup(h => h.Groups).Returns(_groupsMock.Object);
        _loggerMock = new Mock<ILogger<LogTailService>>();
    }

    public void Dispose()
    {
        try { if (File.Exists(_tempFile)) File.Delete(_tempFile); } catch { }
    }

    [Fact]
    public async Task SubscribeAsync_SendsInitialTailBlockToClient()
    {
        using var service = new LogTailService(_filesMock.Object, _hubMock.Object, _loggerMock.Object);

        await service.SubscribeAsync("conn-1", "test.log", CancellationToken.None);

        // Allow background SendInitialTailAsync task to complete
        await Task.Delay(200);

        _clientsMock.Verify(c => c.Client("conn-1"), Times.AtLeastOnce());
        _clientProxyMock.Verify(p => p.SendCoreAsync(
            "LogAppended",
            It.Is<object[]>(args => args.Length > 0 && args[0].ToString()!.Contains("Line 2: ready")),
            It.IsAny<CancellationToken>()),
            Times.AtLeastOnce());
    }

    [Fact]
    public async Task UnsubscribeAsync_RemovesClientFromGroup()
    {
        using var service = new LogTailService(_filesMock.Object, _hubMock.Object, _loggerMock.Object);

        await service.SubscribeAsync("conn-2", "test.log", CancellationToken.None);
        await service.UnsubscribeAsync("conn-2");

        _groupsMock.Verify(g => g.RemoveFromGroupAsync("conn-2", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task AppendingFile_TriggersPumpAndSendsChunkToGroup()
    {
        using var service = new LogTailService(_filesMock.Object, _hubMock.Object, _loggerMock.Object);

        await service.SubscribeAsync("conn-3", "test.log", CancellationToken.None);
        await Task.Delay(100);

        // Append new line to file
        await File.AppendAllTextAsync(_tempFile, "Line 3: dynamic append event\n");

        // Wait for debounce window (150ms) + buffer
        await Task.Delay(400);

        _clientsMock.Verify(c => c.Group(It.IsAny<string>()), Times.AtLeastOnce());
        _clientProxyMock.Verify(p => p.SendCoreAsync(
            "LogAppended",
            It.Is<object[]>(args => args.Length > 0 && args[0].ToString()!.Contains("dynamic append event")),
            It.IsAny<CancellationToken>()),
            Times.AtLeastOnce());
    }
}
