using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using MyLog.Models;

namespace MyLog.Controllers;

public sealed class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;

    public HomeController(ILogger<HomeController> logger)
    {
        _logger = logger;
    }

    public IActionResult Index()
    {
        return View();
    }

    [HttpGet("manifest.json")]
    [ResponseCache(Duration = 3600)]
    public IActionResult Manifest([FromServices] IConfiguration config)
    {
        var envName = config["AppEnvironment"];
        var suffix = string.IsNullOrWhiteSpace(envName) || envName.Equals("PROD", StringComparison.OrdinalIgnoreCase) || envName.Equals("Production", StringComparison.OrdinalIgnoreCase) ? "" : $" [{envName}]";
        
        var themeColor = envName?.ToUpperInvariant() switch
        {
            "SIT" => "#3b82f6", // Blue
            "UAT" => "#eab308", // Yellow
            _ => "#22c55e"      // Green
        };

        return Json(new
        {
            name = $"MyLog - Field Console{suffix}",
            short_name = $"MyLog{suffix}",
            description = "Log workspace and file browser.",
            start_url = "./",
            display = "standalone",
            background_color = "#fdfbf7",
            theme_color = themeColor,
            icons = new[]
            {
                new { src = "icon-192.png", sizes = "192x192", type = "image/png", purpose = "any maskable" },
                new { src = "icon-512.png", sizes = "512x512", type = "image/png", purpose = "any maskable" }
            }
        });
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}

