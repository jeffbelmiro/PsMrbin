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

if (args.Contains("--healthcheck"))
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try { Environment.ExitCode = (await client.GetAsync("http://127.0.0.1:8080/health/ready")).IsSuccessStatusCode ? 0 : 1; }
    catch (HttpRequestException) { Environment.ExitCode = 1; }
    catch (TaskCanceledException) { Environment.ExitCode = 1; }
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => { o.Limits.MaxRequestBodySize = 1600000; o.Limits.MaxConcurrentConnections = 256; });
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
        await context.HttpContext.Response.WriteAsJsonAsync(new { error = "Demasiados pedidos. Aguarde um minuto." }, token);
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
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    try { await next(); }
    catch (Exception ex) when (!context.Response.HasStarted)
    {
        context.Response.Clear();
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.StatusCode = ex switch
        {
            PasteValidationException validation => validation.Status,
            BadHttpRequestException bad => bad.StatusCode,
            JsonException or DecoderFallbackException => 400,
            RedisException => 503,
            _ => 500
        };
        // Never log bodies, Redis exception payloads, or user content.
        var message = ex is PasteValidationException v ? v.Message : context.Response.StatusCode switch
        {
            400 => "Pedido inválido. Verifique o JSON e a codificação UTF-8.",
            413 => "O pedido ultrapassa o tamanho permitido.",
            503 => "Armazenamento temporariamente indisponível. Tente novamente.",
            _ => "Não foi possível processar o pedido."
        };
        await context.Response.WriteAsJsonAsync(new { error = message });
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
