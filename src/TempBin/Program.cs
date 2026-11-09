using TempBin.Guards;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TempBin.Endpoints;
using TempBin.Models;
using TempBin.Services;
using TempBin.Storage;
using TempBin.Localization;
using Microsoft.AspNetCore.Localization;

if (args.Contains("--healthcheck"))
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try { Environment.ExitCode = (await client.GetAsync("http://127.0.0.1:8080/health/ready")).IsSuccessStatusCode ? 0 : 1; }
    catch (HttpRequestException) { Environment.ExitCode = 1; }
    catch (TaskCanceledException) { Environment.ExitCode = 1; }
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddKeyPerFile(Path.GetFullPath("/run/secrets"), optional: true);
builder.Services.Configure<RequestLocalizationOptions>(Text.Configure);
builder.WebHost.ConfigureKestrel(o => { o.AddServerHeader = false; o.Limits.MaxRequestBodySize = 1600000; o.Limits.MaxConcurrentConnections = 256; });
builder.Services.AddOptions<TempBinOptions>().BindConfiguration("TempBin")
    .Validate(o => TempBinOptions.ValidOrigin(o.PublicOrigin), "PublicOrigin deve ser uma origem HTTP(S), sem caminho.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.KeyPrefix), "KeyPrefix é obrigatório.")
    .Validate(o => o.KnownProxies.All(ip => System.Net.IPAddress.TryParse(ip, out _)), "KnownProxies deve conter apenas endereços IP.")
    .Validate(o => ClientIdentity.ValidKey(o.DeletionKey), "DeletionKey deve conter pelo menos 32 bytes em Base64.").ValidateOnStart();
builder.Services.AddOptions<NetcatOptions>().BindConfiguration("Netcat")
    .Validate(o => System.Net.IPAddress.TryParse(o.ListenAddress, out _) && o.Port is >= 1 and <= 65535 && o.PublicPort is >= 1 and <= 65535
        && o.IdleTimeoutMilliseconds is >= 100 and <= 10000 && o.TotalTimeoutSeconds is >= 1 and <= 60,
        "Configuração Netcat inválida.")
    .Validate(o => o.PublicHost is null || Uri.CheckHostName(o.PublicHost) != UriHostNameType.Unknown, "PublicHost deve ser um hostname ou IP.").ValidateOnStart();
builder.Services.AddSingleton<ClientIdentity>();
builder.Services.AddHostedService<NetcatServer>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.AddSingleton<IConnectionMultiplexer>(sp => ConnectionMultiplexer.Connect(sp.GetRequiredService<IOptions<TempBinOptions>>().Value.Redis));
builder.Services.AddSingleton<IPasteStore, RedisPasteStore>();
builder.Services.AddSingleton<ISlugGenerator, SlugGenerator>();
builder.Services.AddSingleton<PasteService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsJsonAsync(new { error = Text.Get("error.rate") }, token);
    };
    // A global cap limits distributed traffic and concurrent bodies; per-IP caps limit enumeration.
    o.GlobalLimiter = PartitionedRateLimiter.CreateChained(
        PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetConcurrencyLimiter("global", _ => new() { PermitLimit = 64, QueueLimit = 0 })),
        PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetFixedWindowLimiter("global", _ => new() { PermitLimit = 3000, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
    o.AddPolicy("create", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
        new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("read", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
        new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
var knownProxies = app.Services.GetRequiredService<IOptions<TempBinOptions>>().Value.KnownProxies;
if (knownProxies.Length > 0)
{
    var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, ForwardLimit = 1 };
    forwarded.KnownNetworks.Clear(); forwarded.KnownProxies.Clear();
    foreach (var ip in knownProxies) forwarded.KnownProxies.Add(System.Net.IPAddress.Parse(ip));
    app.UseForwardedHeaders(forwarded);
}
app.UseRequestLocalization();
app.Use(async (context, next) =>
{
    if (HttpMethods.IsGet(context.Request.Method) && context.Request.Query["lang"].Count == 1
        && Text.Resolve(context.Request.Query["lang"][0]) is { } language)
        context.Response.Cookies.Append(CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(language)),
            new CookieOptions { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromDays(365), Path = "/" });
    ResponseSecurity.Apply(context.Response);
    try { await next(); }
    catch (Exception ex) when (!context.Response.HasStarted)
    {
        context.Response.Clear();
        ResponseSecurity.Apply(context.Response);
        context.Response.Headers.ContentLanguage = Text.Locale;
        context.Response.StatusCode = ex switch
        {
            PasteValidationException validation => validation.Status,
            BadHttpRequestException bad => bad.StatusCode,
            JsonException or DecoderFallbackException => 400,
            RedisException => 503,
            _ => 500
        };
        // Never log bodies, Redis exception payloads, or user content.
        var key = ex is PasteValidationException v ? v.Message : context.Response.StatusCode switch
        {
            400 => "error.invalid",
            413 => "error.bodySize",
            503 => "error.storage",
            _ => "error.internal"
        };
        await context.Response.WriteAsJsonAsync(new { error = Text.Get(key) });
    }
});
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseSwagger();
app.MapGet("/openapi", () => Results.Redirect("/swagger/v1/swagger.json"));
app.MapTempBin();
app.Run();
public partial class Program { }
