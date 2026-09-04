using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MyLog.Models;
using MyLog.Options;
using MyLog.Services;
using Xunit;

namespace MyLog.Tests;

public class ArchiveLogTests : IDisposable
{
    private readonly string _testDir;

    public ArchiveLogTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "MyLog_ArchiveTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, true); } catch { }
    }

    private FileBrowserService CreateService()
    {
        var mockEnv = new Mock<IHostEnvironment>();
        mockEnv.Setup(e => e.ContentRootPath).Returns(_testDir);
        var options = Microsoft.Extensions.Options.Options.Create(new FileBrowserOptions
        {
            AllowedFolders = new[] { _testDir }
        });
        return new FileBrowserService(options, mockEnv.Object, NullLogger<FileBrowserService>.Instance);
    }

    [Fact]
    public async Task ReadContentAsync_ZipWithSingleLogFile_DecompressesAndReturnsLogText()
    {
        // Arrange
        var zipPath = Path.Combine(_testDir, "TEP_SUBMIT2025-07-15.009.zip");
        const string expectedLog = "2025-07-15 10:00:01 [INFO] Initializing batch processor\n2025-07-15 10:00:05 [ERROR] Connection timed out";
        
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("TEP_SUBMIT2025-07-15.009.log");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(expectedLog);
        }

        var service = CreateService();

        // Act
        var result = await service.ReadContentAsync(zipPath, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("=== Archive: TEP_SUBMIT2025-07-15.009.zip", result.Content);
        Assert.Contains("TEP_SUBMIT2025-07-15.009.log", result.Content);
        Assert.Contains("Initializing batch processor", result.Content);
        Assert.Contains("Connection timed out", result.Content);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public async Task ReadContentAsync_EmptyZipFile_ReturnsEmptyNoticeInsteadOfError()
    {
        // Arrange
        var zipPath = Path.Combine(_testDir, "empty_archive.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create)) { }

        var service = CreateService();

        // Act
        var result = await service.ReadContentAsync(zipPath, CancellationToken.None);

        // Assert
        Assert.Contains("The archive is empty", result.Content);
    }

    [Fact]
    public async Task ReadContentAsync_BinaryFileWithNullBytes_ReturnsBinaryNoticeInsteadOfGarbage()
    {
        // Arrange
        var binPath = Path.Combine(_testDir, "sample.bin");
        File.WriteAllBytes(binPath, new byte[] { 0x4D, 0x5A, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00 });

        var service = CreateService();

        // Act
        var result = await service.ReadContentAsync(binPath, CancellationToken.None);

        // Assert
        Assert.Contains("[Notice: This file is a binary file.", result.Content);
    }

    [Fact]
    public async Task SearchContentAsync_InsideZip_FindsMatchingLines()
    {
        // Arrange
        var zipPath = Path.Combine(_testDir, "archive_search.zip");
        const string logData = "line 1 info\nline 2 PaymentError: insufficient funds\nline 3 info";

        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("inner.log");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(logData);
        }

        var service = CreateService();

        // Act
        var results = await service.SearchContentAsync(zipPath, "PaymentError", false, 0, CancellationToken.None);

        // Assert
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Line.Contains("PaymentError"));
    }

    [Fact]
    public async Task SearchFolderAsync_FindsMatchesInsideZippedLogs()
    {
        // Arrange
        var zipPath = Path.Combine(_testDir, "TEP_SUBMIT2025-07-15.009.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("TEP_SUBMIT2025-07-15.009.log");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write("2025-07-15 [ERROR] SocketTimeout on RequestId-9912");
        }

        var service = CreateService();

        // Act
        var results = await service.SearchFolderAsync(_testDir, "RequestId-9912", false, CancellationToken.None);

        // Assert
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.FileName.Contains("TEP_SUBMIT2025-07-15.009.zip") && r.Line.Contains("SocketTimeout"));
    }

    [Fact]
    public void GetContentType_ReturnsApplicationZip_ForZipExtension()
    {
        var service = CreateService();
        var ct = service.GetContentType("test.zip");
        Assert.Equal("application/zip", ct);
    }

    [Fact]
    public async Task ReadContentAsync_GZipFile_DecompressesSuccessfully()
    {
        // Arrange
        var gzPath = Path.Combine(_testDir, "server.log.gz");
        const string rawText = "2026-09-04 12:00:00 [INFO] GZip stream decompression test passed";
        using (var fs = new FileStream(gzPath, FileMode.Create, FileAccess.Write))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        using (var writer = new StreamWriter(gz, Encoding.UTF8))
        {
            writer.Write(rawText);
        }

        var service = CreateService();

        // Act
        var result = await service.ReadContentAsync(gzPath, CancellationToken.None);

        // Assert
        Assert.Contains("Compressed Log (GZip)", result.Content);
        Assert.Contains("GZip stream decompression test passed", result.Content);
    }

    [Fact]
    public async Task ListAsync_IncludesZipFile_WithZipExtension()
    {
        // Arrange
        var zipPath = Path.Combine(_testDir, "TEP_SUBMIT2025-07-15.000.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("inner.log");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write("sample text");
        }

        var service = CreateService();

        // Act
        var list = await service.ListAsync(_testDir, CancellationToken.None);

        // Assert
        var zipFile = Assert.Single(list.Files, f => f.Name == "TEP_SUBMIT2025-07-15.000.zip");
        Assert.Equal(".zip", zipFile.Extension, ignoreCase: true);
    }

    [Fact]
    public void ClusterFileItemDto_StoresExtensionProperly()
    {
        var item = new ClusterFileItemDto(
            "TEP_SUBMIT2025-07-15.000.zip",
            "TEP_SUBMIT2025-07-15.000.zip",
            1024,
            DateTime.UtcNow,
            new[] { "node-1" },
            new[] { "Node 1" },
            ".zip");

        Assert.Equal(".zip", item.Extension);
        Assert.Equal("TEP_SUBMIT2025-07-15.000.zip", item.Name);
    }
}
