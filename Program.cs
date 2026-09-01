using MyLog.Hubs;
using MyLog.Options;
using MyLog.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddSignalR();
builder.Services.Configure<FileBrowserOptions>(builder.Configuration);
builder.Services.AddSingleton<IFileBrowserService, FileBrowserService>();
builder.Services.AddSingleton<ILogTailService, LogTailService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapHub<LogHub>("/hubs/log");

app.Run();
