using Microsoft.AspNetCore.Mvc;
using MyLog.Services;

namespace MyLog.Controllers;

[ApiController]
[Route("api/files")]
public sealed class FilesController : ControllerBase
{
    private readonly IFileBrowserService _files;
    private readonly ILogger<FilesController> _logger;

    public FilesController(IFileBrowserService files, ILogger<FilesController> logger)
    {
        _files = files;
        _logger = logger;
    }

    /// <summary>The configured roots that exist here, so the browser can reach every usable folder.</summary>
    [HttpGet("roots")]
    public async Task<IActionResult> Roots(CancellationToken cancellationToken) => Ok(await _files.GetRootsAsync(cancellationToken));

    [HttpGet("list")]
    public async Task<IActionResult> List([FromQuery] string? path, CancellationToken cancellationToken)
    {
        try { return Ok(await _files.ListAsync(path, cancellationToken)); }
        catch (Exception exception) when (IsClientError(exception)) { return BadRequest(new { error = exception.Message }); }
    }

    [HttpGet("content")]
    public async Task<IActionResult> Content([FromQuery] string path, CancellationToken cancellationToken)
    {
        try { return Ok(await _files.ReadContentAsync(path, cancellationToken)); }
        catch (Exception exception) when (IsClientError(exception)) { return BadRequest(new { error = exception.Message }); }
    }

    [HttpGet("tail")]
    public async Task<IActionResult> Tail(
        [FromQuery] string path,
        [FromQuery] long position = 0,
        [FromQuery] int maxLines = 1000,
        CancellationToken cancellationToken = default)
    {
        try { return Ok(await _files.GetTailAsync(path, position, maxLines, cancellationToken)); }
        catch (Exception exception) when (IsClientError(exception)) { return BadRequest(new { error = exception.Message }); }
    }

        [HttpGet("download")]
    public async Task<IActionResult> Download([FromQuery] string path, CancellationToken cancellationToken)
    {
        try
        {
            var safePath = _files.GetSafeFilePath(path);
            
            Response.ContentType = _files.GetContentType(safePath);
            Response.Headers.Append("Content-Disposition", $"attachment; filename={Path.GetFileName(safePath)}");
            
            using var stream = new FileStream(safePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, useAsync: true);
            await stream.CopyToAsync(Response.Body, 1024 * 1024, cancellationToken);
            
            return new EmptyResult();
        }
        catch (Exception exception) when (IsClientError(exception)) { return BadRequest(new { error = exception.Message }); }
    }

    [HttpGet("download-folder")]
    public async Task<IActionResult> DownloadFolder([FromQuery] string? path, CancellationToken cancellationToken)
    {
        try
        {
            var safePath = _files.GetSafeDirectoryPath(path ?? "");
            var folderName = Path.GetFileName(safePath);
            if (string.IsNullOrEmpty(folderName)) folderName = "logs";

            Response.ContentType = "application/zip";
            Response.Headers.Append("Content-Disposition", $"attachment; filename={folderName}.zip");

            var syncIoFeature = HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>();
            if (syncIoFeature != null) syncIoFeature.AllowSynchronousIO = true;

            await ZipArchiveHelper.ArchiveDirectoryAsync(safePath, Response.Body, cancellationToken);
            return new EmptyResult();
        }
        catch (Exception exception) when (IsClientError(exception)) { return BadRequest(new { error = exception.Message }); }
    }
    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string path,
        [FromQuery] string query,
        [FromQuery] bool regex = false,
        [FromQuery] int context = 0,
        CancellationToken cancellationToken = default)
    {
        try { return Ok(await _files.SearchContentAsync(path, query, regex, context, cancellationToken)); }
        catch (Exception exception) when (IsClientError(exception)) { return BadRequest(new { error = exception.Message }); }
    }

    [HttpGet("analytics")]
    public async Task<IActionResult> Analytics(
        [FromQuery] string path,
        [FromServices] ILogAnalyticsService analytics,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return BadRequest(new { error = "Path is required." });
            var result = await analytics.AnalyzeAsync(path, cancellationToken);
            return Ok(result);
        }
        catch (Exception exception) when (IsClientError(exception))
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpGet("search-folder")]
    public async Task<IActionResult> SearchFolder(
        [FromQuery] string path,
        [FromQuery] string query,
        [FromQuery] bool regex = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(query))
                return BadRequest(new { error = "Both path and query are required." });

            return Ok(await _files.SearchFolderAsync(path, query, regex, cancellationToken));
        }
        catch (Exception exception) when (IsClientError(exception))
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    // A malformed path used to escape these filters and surface as a 500.
    private static bool IsClientError(Exception exception) => exception
        is UnauthorizedAccessException
        or FileNotFoundException
        or DirectoryNotFoundException
        or IOException
        or ArgumentException
        or NotSupportedException;
}











