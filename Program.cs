using MyLog.Hubs;
using MyLog.Options;
using MyLog.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddSignalR();
builder.Services.AddResponseCompression(options => { options.EnableForHttps = true; });

builder.Services.Configure<FileBrowserOptions>(builder.Configuration);
builder.Services.Configure<RemoteLogOptions>(builder.Configuration.GetSection("RemoteLogApi"));
builder.Services.Configure<ClusterOptions>(builder.Configuration);

builder.Services.AddSingleton<IFileBrowserService, FileBrowserService>();
builder.Services.AddSingleton<ILogAnalyticsService, LogAnalyticsService>();
builder.Services.AddSingleton<ILogTailService, LogTailService>();
builder.Services.AddSingleton<IClusterLogService, ClusterLogService>();

// Configure HttpClient for RemoteLogService
builder.Services.AddHttpClient<IRemoteLogService, RemoteLogService>((serviceProvider, client) =>
{
    var options = builder.Configuration.GetSection("RemoteLogApi").Get<RemoteLogOptions>();
    if (options != null && !string.IsNullOrEmpty(options.BaseUrl))
    {
        client.BaseAddress = new Uri(options.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    }

    // Default Accept header for JSON APIs
    client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

    // Inject API-KEY
    if (options != null && !string.IsNullOrEmpty(options.ApiKey))
    {
        client.DefaultRequestHeaders.Add("API-KEY", options.ApiKey);
    }

    // Inject Cookie if configured (e.g., for internal SIT gateways)
    if (options != null && !string.IsNullOrEmpty(options.Cookie))
    {
        client.DefaultRequestHeaders.Add("Cookie", options.Cookie);
    }
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    // Internal gateways often use self-signed or internal CA certificates.
    // Bypass certificate validation for these endpoints.
    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
});

// Configure HttpClient for Cluster Peer-to-Peer calls across load-balanced nodes
builder.Services.AddHttpClient("ClusterPeerClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseStaticFiles();

app.UseResponseCompression();

app.UseRouting();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapHub<LogHub>("/hubs/log");

app.Run();
