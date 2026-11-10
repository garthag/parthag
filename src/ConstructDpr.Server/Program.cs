using System.Threading.RateLimiting;
using ConstructDpr.Core.Data;
using ConstructDpr.Server;
using ConstructDpr.Shared.Api;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // As a Windows service the working folder is C:\Windows\System32: settings and data stay next to the program.
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Host.UseWindowsService(o => o.ServiceName = "CONSTRUCT DPR sync server"); // no effect when started normally
var cfg = builder.Configuration;

// ── Database: PostgreSQL (shared server) or SQLite (one site-office PC) ──
var (provider, connection) = ServerSettings.Database(cfg, builder.Environment.ContentRootPath);
var dbOptions = AppDbContext.Options(provider, connection);
builder.Services.AddSingleton(dbOptions);
builder.Services.AddScoped(_ => new AppDbContext(dbOptions));

// ── Sign-in: ASP.NET Core Identity with bearer tokens (web, Android and Windows apps all use the same tokens) ──
builder.Services.AddAuthorization();
builder.Services.AddIdentityApiEndpoints<AppUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 8;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = false;
        o.Lockout.MaxFailedAccessAttempts = 8;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
    })
    .AddEntityFrameworkStores<AppDbContext>();
builder.Services.Configure<BearerTokenOptions>(IdentityConstants.BearerScheme, o =>
{
    o.BearerTokenExpiration = TimeSpan.FromHours(8);
    o.RefreshTokenExpiration = TimeSpan.FromDays(30);
    // Browsers cannot send headers on WebSocket connections: SignalR passes the token as ?access_token=…
    o.Events = new BearerTokenEvents
    {
        OnMessageReceived = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/hubs") && ctx.Request.Query.TryGetValue("access_token", out var t)) ctx.Token = t;
            return Task.CompletedTask;
        },
    };
});
builder.Services.AddDataProtection().PersistKeysToDbContext<AppDbContext>().SetApplicationName("ConstructDpr");

// ── API plumbing ──
builder.Services.AddSignalR();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => Json.Configure(o.SerializerOptions));
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear(); // hosting platforms (Render, Azure, a reverse proxy) terminate HTTPS in front of the app
    o.KnownProxies.Clear();
});
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("signin", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.AddSingleton(ServerSettings.Features(cfg));
builder.Services.AddSingleton(new SiteClock(cfg["Site:TimeZone"] ?? "Asia/Kolkata"));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ApiFactory>();
builder.Services.AddSingleton<ChangeWatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChangeWatcher>());

var app = builder.Build();

// Create the tables on first start (retry while the database container is still starting).
for (var attempt = 1; ; attempt++)
{
    try
    {
        using var db = new AppDbContext(dbOptions);
        db.EnsureSchema();
        break;
    }
    catch (Exception ex) when (attempt < 10)
    {
        app.Logger.LogWarning("Database not ready ({Message}); retrying in 3 s", ex.GetBaseException().Message);
        Thread.Sleep(3000);
    }
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseMiddleware<ApiErrors>();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", version = ServerSettings.Version }));
app.MapAccount();
app.MapApi();
app.MapHub<LiveHub>("/hubs/live");
app.MapGet("/", () => Results.Text($"CONSTRUCT DPR sync server {ServerSettings.Version} is running. Connect the Android or Windows app to this address."));
app.Map("/api/{**rest}", () => Results.NotFound());

app.Run();

public partial class Program;
