using Microsoft.AspNetCore.Mvc;
using MyLog.Services;

namespace MyLog.Controllers;

[ApiController]
[Route("api/files")]
public sealed class FilesController : ControllerBase
{
    private readonly IFileBrowserService _files;

    public FilesController(IFileBrowserService files) => _files = files;

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

    [HttpGet("download")]
    public IActionResult Download([FromQuery] string path)
    {
        try
        {
            var safePath = _files.GetSafeFilePath(path);
            return PhysicalFile(safePath, _files.GetContentType(safePath), Path.GetFileName(safePath), enableRangeProcessing: true);
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

    // A malformed path used to escape these filters and surface as a 500.
    private static bool IsClientError(Exception exception) => exception
        is UnauthorizedAccessException
        or FileNotFoundException
        or DirectoryNotFoundException
        or IOException
        or ArgumentException
        or NotSupportedException;
}
