using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TempBin.Models;
using TempBin.Services;
using TempBin.Storage;
using TempBin.Web;

namespace TempBin.Endpoints;

public static class TempBinEndpoints
{
    public static object Links(Paste paste, string origin) => new
    {
        paste.Slug, url = $"{origin}/{paste.Slug}", raw_url = $"{origin}/raw/{paste.Slug}",
        api_url = $"{origin}/api/v1/pastes/{paste.Slug}", download_url = $"{origin}/download/{paste.Slug}",
        created_at = paste.CreatedAt, expires_at = paste.ExpiresAt, ttl_seconds = PasteService.TtlSeconds
    };

    public static void MapTempBin(this WebApplication app)
    {
        app.MapGet("/", (IOptions<TempBinOptions> options) => Results.Content(Html.Home(options.Value.PublicOrigin.TrimEnd('/')), "text/html; charset=utf-8"));
        app.MapGet("/robots.txt", () => Results.Text("User-agent: *\nDisallow: /\n", "text/plain"));
        app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
        app.MapGet("/health/ready", async (IPasteStore store) => await store.IsHealthyAsync()
            ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));
        app.MapPost("/api/v1/pastes", async (HttpRequest request, PasteService service, IOptions<TempBinOptions> options) =>
        {
            string? content;
            var length = 8;
            var mediaType = request.ContentType?.Split(';')[0].Trim().ToLowerInvariant();
            if (mediaType is not ("application/json" or "text/plain"))
                return Results.Json(new { error = "Use application/json ou text/plain em UTF-8." }, statusCode: 415);
            if (request.ContentType!.Contains("charset=", StringComparison.OrdinalIgnoreCase)
                && !request.ContentType.Replace("\"", "").Replace(" ", "").EndsWith("charset=utf-8", StringComparison.OrdinalIgnoreCase))
                throw new PasteValidationException("A codificação deve ser UTF-8.");
            // Hard byte cap before deserialization, including chunked requests and JSON escaping overhead.
            var cap = mediaType == "text/plain" ? PasteService.MaxBytes : 1600000;
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await request.Body.ReadAsync(chunk, request.HttpContext.RequestAborted)) > 0)
            {
                if (buffer.Length + read > cap) throw new PasteValidationException("O pedido ultrapassa o tamanho permitido.", 413);
                buffer.Write(chunk, 0, read);
            }
            var body = new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            if (mediaType == "application/json")
            {
                var payload = JsonSerializer.Deserialize<CreatePasteRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (payload is null) throw new PasteValidationException("O payload JSON é obrigatório.");
                content = payload.Content;
                length = payload.SlugLength;
            }
            else
            {
                content = body;
                if (request.Query.TryGetValue("slug_length", out var requested))
                {
                    if (!int.TryParse(requested, out length)) throw new PasteValidationException("Comprimento de slug inválido.");
                }
            }
            var paste = await service.CreateAsync(content, length);
            var origin = options.Value.PublicOrigin.TrimEnd('/');
            return Results.Created($"{origin}/{paste.Slug}", Links(paste, origin));
        }).RequireRateLimiting("create").Accepts<CreatePasteRequest>("application/json", "text/plain")
          .Produces(201).Produces(400).Produces(413).Produces(415).Produces(429);

        app.MapGet("/api/v1/pastes/{slug}", async (string slug, PasteService service, IOptions<TempBinOptions> options) =>
        {
            var result = await service.FindAsync(slug);
            return result.Paste is { } paste ? Results.Ok(new { paste.Slug, paste.Content, paste.CreatedAt, paste.ExpiresAt, paste.SizeBytes, paste.Version,
                links = Links(paste, options.Value.PublicOrigin.TrimEnd('/')) }) : Missing(result);
        }).RequireRateLimiting("read");
        app.MapGet("/api/v1/pastes/{slug}/meta", async (string slug, PasteService service) =>
        {
            var result = await service.FindAsync(slug);
            return result.Paste is { } paste ? Results.Ok(new { paste.Slug, paste.CreatedAt, paste.ExpiresAt, paste.SizeBytes, paste.Version }) : Missing(result);
        }).RequireRateLimiting("read");
        app.MapGet("/raw/{slug}", async (string slug, PasteService service) =>
        {
            var result = await service.FindAsync(slug);
            return result.Paste is { } paste ? Results.Text(paste.Content, "text/plain; charset=utf-8") : Missing(result);
        }).RequireRateLimiting("read");
        app.MapGet("/download/{slug}", async (string slug, PasteService service) =>
        {
            var result = await service.FindAsync(slug);
            return result.Paste is { } paste ? Results.File(Encoding.UTF8.GetBytes(paste.Content), "text/plain; charset=utf-8", $"tempbin-{paste.Slug}.txt") : Missing(result);
        }).RequireRateLimiting("read");
        app.MapGet("/{slug}", async (string slug, PasteService service, IOptions<TempBinOptions> options) =>
        {
            var result = await service.FindAsync(slug);
            if (result.Paste is { } paste) return Results.Content(Html.Result(paste, options.Value.PublicOrigin.TrimEnd('/')), "text/html; charset=utf-8");
            var expired = result.State == PasteState.Expired;
            return Results.Content(Html.Error(expired), "text/html; charset=utf-8", statusCode: expired ? 410 : 404);
        }).RequireRateLimiting("read");
    }
    private static IResult Missing(PasteLookup result) => Results.Json(new { error = result.State == PasteState.Expired
        ? "Este texto expirou e não pode ser recuperado." : "Texto não encontrado." }, statusCode: result.State == PasteState.Expired ? 410 : 404);
}
