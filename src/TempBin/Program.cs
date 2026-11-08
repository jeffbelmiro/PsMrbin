using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
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
    .Validate(o => !string.IsNullOrWhiteSpace(o.KeyPrefix), "KeyPrefix é obrigatório.").ValidateOnStart();
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
