using Microsoft.AspNetCore.Mvc;
using MyLog.Services;

namespace MyLog.Controllers;

[ApiController]
[Route("api/remote")]
public sealed class RemoteLogController : ControllerBase
{
    private readonly IRemoteLogService _remoteLogService;

    public RemoteLogController(IRemoteLogService remoteLogService)
    {
        _remoteLogService = remoteLogService;
    }

    [HttpGet("projects")]
    public async Task<IActionResult> GetProjects(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _remoteLogService.GetProjectsAsync(cancellationToken));
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("files")]
    public async Task<IActionResult> GetFiles([FromQuery] string projectName, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _remoteLogService.GetFilesAsync(projectName, cancellationToken));
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("content")]
    public async Task<IActionResult> GetContent([FromQuery] string projectName, [FromQuery] string fileName, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _remoteLogService.BrowseFileAsync(projectName, fileName, cancellationToken));
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string projectName, [FromQuery] string fileName, [FromQuery] string query, [FromQuery] bool regex, [FromQuery] int context, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _remoteLogService.SearchContentAsync(projectName, fileName, query, regex, context, cancellationToken));
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("analytics")]
    public async Task<IActionResult> GetAnalytics([FromQuery] string projectName, [FromQuery] string fileName, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _remoteLogService.AnalyzeRemoteFileAsync(projectName, fileName, cancellationToken));
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("download")]
    public async Task<IActionResult> Download([FromQuery] string projectName, [FromQuery] string fileName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(fileName))
        {
            return BadRequest(new { error = "projectName and fileName are required." });
        }

        try
        {
            var (stream, resolvedName) = await _remoteLogService.DownloadFileAsync(projectName, fileName, cancellationToken);
            return File(stream, "application/octet-stream", resolvedName);
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("download-folder")]
    public async Task<IActionResult> DownloadFolder([FromQuery] string projectName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return BadRequest(new { error = "projectName is required." });
        }

        try
        {
            var stream = await _remoteLogService.DownloadProjectZipAsync(projectName, cancellationToken);
            var safeProjectName = projectName.Replace('/', '_').Replace('\\', '_');
            return File(stream, "application/zip", $"remote-{safeProjectName}-logs.zip");
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
