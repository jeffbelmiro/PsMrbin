using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TempBin.Models;
using TempBin.Services;
using TempBin.Storage;
using TempBin.Web;
using TempBin.Localization;

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
        app.MapGet("/", (IOptions<TempBinOptions> options, IOptions<NetcatOptions> netcat) => Results.Content(Html.Home(options.Value.PublicOrigin.TrimEnd('/'), netcat.Value), "text/html; charset=utf-8"));
        app.MapGet("/robots.txt", () => Results.Text("User-agent: *\nDisallow: /\n", "text/plain"));
        app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
        app.MapGet("/health/ready", async (IPasteStore store) => await store.IsHealthyAsync()
            ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));
        app.MapPost("/api/v1/pastes", async (HttpRequest request, PasteService service, ClientIdentity identity, IOptions<TempBinOptions> options) =>
        {
            string? content;
            var length = 8;
            var mediaType = request.ContentType?.Split(';')[0].Trim().ToLowerInvariant();
            if (mediaType is not ("application/json" or "text/plain"))
                return Results.Json(new { error = Text.Get("error.media") }, statusCode: 415);
            if (request.ContentType!.Contains("charset=", StringComparison.OrdinalIgnoreCase)
                && !request.ContentType.Replace("\"", "").Replace(" ", "").EndsWith("charset=utf-8", StringComparison.OrdinalIgnoreCase))
                throw new PasteValidationException("error.encoding");
            // Hard byte cap before deserialization, including chunked requests and JSON escaping overhead.
            var cap = mediaType == "text/plain" ? PasteService.MaxBytes : 1600000;
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await request.Body.ReadAsync(chunk, request.HttpContext.RequestAborted)) > 0)
            {
                if (buffer.Length + read > cap) throw new PasteValidationException("error.bodySize", 413);
                buffer.Write(chunk, 0, read);
            }
            var body = new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            if (mediaType == "application/json")
            {
                var payload = JsonSerializer.Deserialize<CreatePasteRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (payload is null) throw new PasteValidationException("error.json");
                content = payload.Content;
                length = payload.SlugLength;
            }
            else
            {
                content = body;
                if (request.Query.TryGetValue("slug_length", out var requested))
                {
                    if (!int.TryParse(requested, out length)) throw new PasteValidationException("error.slugValue");
                }
            }
            var paste = await service.CreateAsync(content, length, identity.Fingerprint(request.HttpContext.Connection.RemoteIpAddress));
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
        app.MapDelete("/api/v1/pastes/{slug}", DeleteAsync).RequireRateLimiting("create");
        app.MapPost("/delete", DeleteAsync).RequireRateLimiting("create");
        app.MapGet("/{slug}", async (string slug, HttpRequest request, PasteService service, IOptions<TempBinOptions> options) =>
        {
            var result = await service.FindAsync(slug);
            if (result.Paste is { } paste)
            {
                var accept = request.GetTypedHeaders().Accept;
                var wantsHtml = accept?.Any(a => a.MediaType.Value == "text/html" && a.Quality != 0) == true;
                var wantsText = accept?.Any(a => a.MediaType.Value == "text/plain" && a.Quality != 0) == true;
                var agent = request.Headers.UserAgent.ToString();
                if (!wantsHtml && (wantsText || agent.StartsWith("curl/", StringComparison.OrdinalIgnoreCase) || agent.StartsWith("Wget/", StringComparison.OrdinalIgnoreCase)))
                    return Results.Text(paste.Content, "text/plain; charset=utf-8");
                return Results.Content(Html.Result(paste, options.Value.PublicOrigin.TrimEnd('/')), "text/html; charset=utf-8");
            }
            var expired = result.State == PasteState.Expired;
            return Results.Content(Html.Error(expired), "text/html; charset=utf-8", statusCode: expired ? 410 : 404);
        }).RequireRateLimiting("read");
    }
    private static async Task<IResult> DeleteAsync(string slug, HttpRequest request, PasteService service,
        ClientIdentity identity, IOptions<TempBinOptions> options)
    {
        // Prevent a third-party web page deleting a known paste through the visitor's IP.
        if (request.Headers["Sec-Fetch-Site"] == "cross-site" || (request.Headers.TryGetValue("Origin", out var origin)
            && origin.ToString() != options.Value.PublicOrigin.TrimEnd('/')))
            return Results.Json(new { error = Text.Get("error.origin") }, statusCode: 403);
        var result = await service.DeleteAsync(slug, identity.Fingerprint(request.HttpContext.Connection.RemoteIpAddress));
        return result switch
        {
            DeleteState.Deleted => Results.NoContent(),
            DeleteState.Missing => Results.Json(new { error = Text.Get("error.missing") }, statusCode: 404),
            DeleteState.Gone => Results.Json(new { error = Text.Get("error.gone") }, statusCode: 410),
            DeleteState.TooLate => Results.Json(new { error = Text.Get("error.deleteLate") }, statusCode: 403),
            _ => Results.Json(new { error = Text.Get("error.deleteOwner") }, statusCode: 403)
        };
    }
    private static IResult Missing(PasteLookup result) => Results.Json(new { error = Text.Get(result.State == PasteState.Expired
        ? "error.gone" : "error.missing") }, statusCode: result.State == PasteState.Expired ? 410 : 404);
}
