using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using RnxEmailServer;

var russian = CultureInfo.GetCultureInfo("ru-RU");
CultureInfo.DefaultThreadCurrentCulture = russian;
CultureInfo.DefaultThreadCurrentUICulture = russian;

var commandMode = args.FirstOrDefault() == "account";
var builder = WebApplication.CreateBuilder(commandMode ? [] : args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true).AddEnvironmentVariables();
if (!commandMode && args.Length > 0) builder.Configuration.AddCommandLine(args);
var settings = MailSettings.Read(builder.Configuration, builder.Environment.ContentRootPath);
var store = new MailStore(settings);
store.Initialize();

if (commandMode) return AccountCommands.Run(args, settings, store);

var secureWeb = new Uri(settings.WebUrl).Scheme == Uri.UriSchemeHttps;
builder.WebHost.UseUrls(settings.WebUrl);
builder.WebHost.ConfigureKestrel(server =>
{
    server.AddServerHeader = false;
    server.Limits.MaxRequestBodySize = 1_048_576;
});
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(store);
builder.Services.AddHostedService<SmtpReceiver>();
builder.Services.AddHostedService<RelayWorker>();
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("RnxEmailServer")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(settings.DataDirectory, "keys")));
if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "RnxMail.Session";
    options.Cookie.SecurePolicy = secureWeb ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.LoginPath = "/login";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = false;
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "RnxMail.Form";
    options.Cookie.SecurePolicy = secureWeb ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();
var webUrl = new Uri(settings.WebUrl);
if (secureWeb) app.UseHsts();
app.Use(async (context, next) =>
{
    var host = context.Request.Host.Host;
    var isLoopback = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                     IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    if (!string.Equals(host, webUrl.Host, StringComparison.OrdinalIgnoreCase) && !(webUrl.IsLoopback && isLoopback))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var headers = context.Response.Headers;
    headers.ContentSecurityPolicy = "default-src 'none'; style-src 'self'; img-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    await next();
});
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-cache, no-store";
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
WebMail.Map(app);
await app.RunAsync();
return 0;
