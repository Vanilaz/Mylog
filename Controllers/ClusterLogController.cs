using Microsoft.AspNetCore.Mvc;
using MyLog.Services;

namespace MyLog.Controllers;

[ApiController]
[Route("api/cluster")]
public sealed class ClusterLogController : ControllerBase
{
    private readonly IClusterLogService _clusterService;
    private readonly ILogger<ClusterLogController> _logger;

    public ClusterLogController(IClusterLogService clusterService, ILogger<ClusterLogController> logger)
    {
        _clusterService = clusterService;
        _logger = logger;
    }

    [HttpGet("list")]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var clusters = await _clusterService.GetClustersAsync(cancellationToken);
        return Ok(clusters);
    }

    [HttpGet("{clusterId}/browse")]
    public async Task<IActionResult> Browse(
        string clusterId,
        [FromQuery] string? path,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _clusterService.BrowseClusterPathAsync(clusterId, path, cancellationToken);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("{clusterId}/content")]
    public async Task<IActionResult> Content(
        string clusterId,
        [FromQuery] string? path,
        [FromQuery] string fileName,
        [FromQuery] string? nodeId,
        [FromQuery] string? viewMode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return BadRequest(new { message = "fileName is required." });
        }

        try
        {
            var result = await _clusterService.ReadClusterFileAsync(clusterId, path, fileName, nodeId, viewMode, cancellationToken);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("{clusterId}/analytics")]
    public async Task<IActionResult> Analytics(
        string clusterId,
        [FromQuery] string? path,
        [FromQuery] string fileName,
        [FromQuery] string? nodeId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return BadRequest(new { message = "fileName is required." });
        }

        try
        {
            var result = await _clusterService.AnalyzeClusterFileAsync(clusterId, path, fileName, nodeId, cancellationToken);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("{clusterId}/search")]
    public async Task<IActionResult> Search(
        string clusterId,
        [FromQuery] string? path,
        [FromQuery] string fileName,
        [FromQuery] string query,
        [FromQuery] bool regex = false,
        [FromQuery] int context = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(query))
        {
            return Ok(Array.Empty<object>());
        }

        try
        {
            var results = await _clusterService.SearchClusterAsync(clusterId, path, fileName, query, regex, context, cancellationToken);
            return Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search cluster {ClusterId}", clusterId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = ex.Message });
        }
    }

    [HttpGet("{clusterId}/search-folder")]
    public async Task<IActionResult> SearchFolder(
        string clusterId,
        [FromQuery] string? path,
        [FromQuery] string query,
        [FromQuery] bool regex = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest(new { message = "Query is required." });
        }

        try
        {
            var results = await _clusterService.SearchClusterFolderAsync(clusterId, path, query, regex, cancellationToken);
            return Ok(results);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search folder in cluster {ClusterId}", clusterId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = ex.Message });
        }
    }

    [HttpGet("{clusterId}/download-zip")]
    public async Task<IActionResult> DownloadZip(
        string clusterId,
        [FromQuery] string? path,
        [FromQuery] string fileName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return BadRequest(new { message = "fileName is required." });
        }

        try
        {
            var stream = await _clusterService.DownloadClusterZipAsync(clusterId, path, fileName, cancellationToken);
            var safeName = Path.GetFileNameWithoutExtension(fileName);
            return File(stream, "application/zip", $"cluster-{clusterId}-{safeName}.zip");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download cluster zip for {ClusterId} / {FileName}", clusterId, fileName);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = ex.Message });
        }
    }

    [HttpGet("{clusterId}/download-folder")]
    public async Task<IActionResult> DownloadFolder(
        string clusterId,
        [FromQuery] string? path,
        CancellationToken cancellationToken)
    {
        try
        {
            var stream = await _clusterService.DownloadClusterFolderZipAsync(clusterId, path, cancellationToken);
            var safeFolder = string.IsNullOrWhiteSpace(path) ? "all" : Path.GetFileName(path.Trim('/', '\\'));
            return File(stream, "application/zip", $"cluster-{clusterId}-{safeFolder}-logs.zip");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download cluster folder zip for {ClusterId} / {Path}", clusterId, path);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = ex.Message });
        }
    }

    [HttpGet("{clusterId}/download-file")]
    public async Task<IActionResult> DownloadFile(
        string clusterId,
        [FromQuery] string? path,
        [FromQuery] string fileName,
        [FromQuery] string? format,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return BadRequest(new { message = "fileName is required." });
        }

        try
        {
            var safeName = Path.GetFileNameWithoutExtension(fileName);
            if (string.Equals(format, "log", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(format, "concat", StringComparison.OrdinalIgnoreCase))
            {
                var stream = await _clusterService.DownloadClusterConcatLogAsync(clusterId, path, fileName, cancellationToken);
                return File(stream, "text/plain", $"cluster-{clusterId}-{safeName}.log");
            }

            var zipStream = await _clusterService.DownloadClusterZipAsync(clusterId, path, fileName, cancellationToken);
            return File(zipStream, "application/zip", $"cluster-{clusterId}-{safeName}.zip");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download cluster file for {ClusterId} / {FileName}", clusterId, fileName);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = ex.Message });
        }
    }

    [HttpGet("{clusterId}/tail")]
    public async Task<IActionResult> TailCluster(
        string clusterId,
        [FromQuery] string? path,
        [FromQuery] string fileName,
        [FromQuery] string? nodeId,
        [FromQuery] long position = 0,
        [FromQuery] int maxLines = 1000,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return BadRequest(new { message = "fileName is required." });
        }

        try
        {
            var result = await _clusterService.TailClusterFileAsync(clusterId, path, fileName, nodeId, position, maxLines, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to tail cluster file {ClusterId} / {FileName}", clusterId, fileName);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = ex.Message });
        }
    }

    // ── Local Node Endpoints (Called Peer-to-Peer by Other Cluster Nodes) ──

    [HttpGet("node/probe")]
    public IActionResult NodeProbe() => Ok(new { status = "OK", machine = Environment.MachineName });

    [HttpGet("node/browse")]
    public async Task<IActionResult> NodeBrowse([FromQuery] string? path, CancellationToken cancellationToken)
    {
        var result = await _clusterService.LocalNodeBrowseAsync(path, cancellationToken);
        return Ok(result);
    }

    [HttpGet("node/content")]
    public async Task<IActionResult> NodeContent([FromQuery] string? path, [FromQuery] string fileName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return BadRequest(new { message = "fileName is required." });
        var result = await _clusterService.LocalNodeContentAsync(path, fileName, cancellationToken);
        return Ok(result);
    }

    [HttpGet("node/search")]
    public async Task<IActionResult> NodeSearch(
        [FromQuery] string? path,
        [FromQuery] string fileName,
        [FromQuery] string query,
        [FromQuery] bool regex = false,
        [FromQuery] int context = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(query)) return Ok(Array.Empty<object>());
        var result = await _clusterService.LocalNodeSearchAsync(path, fileName, query, regex, context, cancellationToken);
        return Ok(result);
    }

    [HttpGet("node/search-folder")]
    public async Task<IActionResult> NodeSearchFolder(
        [FromQuery] string? path,
        [FromQuery] string query,
        [FromQuery] bool regex = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Ok(Array.Empty<object>());
        var result = await _clusterService.LocalNodeSearchFolderAsync(path, query, regex, cancellationToken);
        return Ok(result);
    }

    [HttpGet("node/download")]
    public async Task<IActionResult> NodeDownload([FromQuery] string? path, [FromQuery] string fileName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return BadRequest(new { message = "fileName is required." });
        var stream = await _clusterService.LocalNodeDownloadAsync(path, fileName, cancellationToken);
        if (stream == null) return NotFound(new { message = "File not found on local node." });
        return File(stream, "application/octet-stream", fileName);
    }

    [HttpGet("node/download-folder")]
    public async Task<IActionResult> NodeDownloadFolder([FromQuery] string? path, CancellationToken cancellationToken)
    {
        var stream = await _clusterService.LocalNodeDownloadFolderAsync(path, cancellationToken);
        if (stream == null) return NotFound(new { message = "Directory not found on local node." });
        var folderName = string.IsNullOrWhiteSpace(path) ? "logs" : Path.GetFileName(path.Trim('/', '\\'));
        return File(stream, "application/zip", $"{folderName}.zip");
    }
}
