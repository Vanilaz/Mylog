using Microsoft.AspNetCore.Mvc;
using MyLog.Services;

namespace MyLog.Controllers;

[ApiController]
[Route("api/files")]
public sealed class FilesController : ControllerBase
{
    private readonly IFileBrowserService _files;

    public FilesController(IFileBrowserService files) => _files = files;

    [HttpGet("list")]
    public async Task<IActionResult> List([FromQuery] string? path, CancellationToken cancellationToken)
    {
        try { return Ok(await _files.ListAsync(path, cancellationToken)); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or DirectoryNotFoundException) { return BadRequest(new { error = exception.Message }); }
    }

    [HttpGet("content")]
    public async Task<IActionResult> Content([FromQuery] string path, CancellationToken cancellationToken)
    {
        try { return Ok(await _files.ReadContentAsync(path, cancellationToken)); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or FileNotFoundException or IOException) { return BadRequest(new { error = exception.Message }); }
    }

    [HttpGet("download")]
    public IActionResult Download([FromQuery] string path)
    {
        try
        {
            var safePath = _files.GetSafeFilePath(path);
            return PhysicalFile(safePath, _files.GetContentType(safePath), Path.GetFileName(safePath), enableRangeProcessing: true);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or FileNotFoundException or IOException) { return BadRequest(new { error = exception.Message }); }
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string path, [FromQuery] string query, [FromQuery] bool regex = false, [FromQuery] int context = 0, CancellationToken cancellationToken = default)
    {
        try { return Ok(await _files.SearchContentAsync(path, query, regex, context, cancellationToken)); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or FileNotFoundException or IOException or ArgumentException) { return BadRequest(new { error = exception.Message }); }
    }

    [HttpPost("truncate")]
    public IActionResult Truncate([FromQuery] string path)
    {
        try 
        { 
            var safePath = _files.GetSafeFilePath(path);
            System.IO.File.WriteAllText(safePath, string.Empty);
            return Ok();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or FileNotFoundException or IOException) { return BadRequest(new { error = exception.Message }); }
    }
}