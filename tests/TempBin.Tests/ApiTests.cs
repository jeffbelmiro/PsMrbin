using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TempBin.Tests;

public class ApiTests
{
    private static async Task<JsonElement> Create(HttpClient client, string content = "Olá, mundo!", int length = 8)
    {
        var response = await client.PostAsJsonAsync("/api/v1/pastes", new { content, slug_length = length });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(json.GetProperty("url").GetString(), response.Headers.Location!.ToString());
        return json;
    }
    [Fact]
    public async Task JsonCreateReturnsAllLinksAndFixedExpiry()
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        var result = await Create(client);
        foreach (var field in new[] { "url", "raw_url", "api_url", "download_url" }) Assert.StartsWith("http://localhost:8080/", result.GetProperty(field).GetString());
        Assert.Equal(1800, result.GetProperty("ttl_seconds").GetInt32());
        Assert.Equal(TimeSpan.FromMinutes(30), result.GetProperty("expires_at").GetDateTimeOffset() - result.GetProperty("created_at").GetDateTimeOffset());
    }
    [Fact]
    public async Task PlainTextAndFileBodyRoundTrip()
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        const string content = "linha 1\r\nOlá 🌍\nlinha 3\n";
        var response = await client.PostAsync("/api/v1/pastes?slug_length=16", new StringContent(content, Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var slug = result.GetProperty("slug").GetString(); Assert.Equal(16, slug!.Length);
        var raw = await client.GetAsync($"/raw/{slug}");
        Assert.Equal(content, await raw.Content.ReadAsStringAsync());
        Assert.Equal("text/plain", raw.Content.Headers.ContentType!.MediaType);
        var file = await client.GetAsync($"/download/{slug}");
        Assert.Equal(Encoding.UTF8.GetBytes(content), await file.Content.ReadAsByteArrayAsync());
        Assert.Contains($"tempbin-{slug}.txt", file.Content.Headers.ContentDisposition!.ToString());
        var json = await client.GetFromJsonAsync<JsonElement>($"/api/v1/pastes/{slug}");
        Assert.Equal(content, json.GetProperty("content").GetString());
        var meta = await client.GetFromJsonAsync<JsonElement>($"/api/v1/pastes/{slug}/meta");
        Assert.False(meta.TryGetProperty("content", out _));
        Assert.Equal(Encoding.UTF8.GetByteCount(content), meta.GetProperty("size_bytes").GetInt32());
    }
    [Theory][InlineData("")][InlineData("  \n ")]
    public async Task EmptyContentIsRejected(string content)
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/pastes", new { content })).StatusCode);
    }
    [Fact]
    public async Task Utf8LimitAcceptsExactMaximumAndRejectsOneByteMore()
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        await Create(client, new string('á', 131072));
        var large = await client.PostAsJsonAsync("/api/v1/pastes", new { content = new string('á', 131072) + "a" });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        var plain = await client.PostAsync("/api/v1/pastes", new StringContent(new string('a', 262145), Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, plain.StatusCode);
    }
    [Theory][InlineData("not-json", "application/json", 400)][InlineData("hello", "text/html", 415)]
    public async Task MalformedOrUnsupportedBodiesAreRejected(string body, string type, int status)
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        Assert.Equal(status, (int)(await client.PostAsync("/api/v1/pastes", new StringContent(body, Encoding.UTF8, type))).StatusCode);
    }
    [Theory][InlineData(4)][InlineData(7)][InlineData(17)]
    public async Task InvalidLengthIsBadRequest(int length)
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/pastes", new { content = "test", slug_length = length })).StatusCode);
    }
    [Theory][InlineData("/abcdefgh")][InlineData("/raw/abcdefgh")][InlineData("/download/abcdefgh")][InlineData("/api/v1/pastes/abcdefgh")][InlineData("/api/v1/pastes/abcdefgh/meta")][InlineData("/raw/INVALID!")]
    public async Task UnknownLinksAre404(string path)
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
    }
    [Fact]
    public async Task AllFormatsBecome410ExactlyAtExpiry()
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        var json = await Create(client); var slug = json.GetProperty("slug").GetString();
        app.Clock.Advance(TimeSpan.FromMinutes(30));
        foreach (var path in new[] { $"/{slug}", $"/raw/{slug}", $"/download/{slug}", $"/api/v1/pastes/{slug}", $"/api/v1/pastes/{slug}/meta" })
            Assert.Equal(HttpStatusCode.Gone, (await client.GetAsync(path)).StatusCode);
    }
    [Fact]
    public async Task HtmlEscapesScriptsAndSetsSecurityHeaders()
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        const string attack = "<script>alert('xss')</script><img src=x onerror=alert(1)>";
        var json = await Create(client, attack);
        var response = await client.GetAsync("/" + json.GetProperty("slug").GetString());
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(attack, html); Assert.Contains("&lt;script&gt;", html);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("script-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("noindex", response.Headers.GetValues("X-Robots-Tag").Single());
    }
    [Fact]
    public async Task HostHeaderDoesNotInfluenceGeneratedUrls()
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        client.DefaultRequestHeaders.Host = "evil.example";
        var json = await Create(client);
        Assert.StartsWith("http://localhost:8080/", json.GetProperty("url").GetString());
    }
    [Fact]
    public async Task CreateRateLimitRejectsTwentyFirstRequest()
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        for (var i = 0; i < 20; i++) await Create(client);
        var response = await client.PostAsJsonAsync("/api/v1/pastes", new { content = "limited" });
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
    }
    [Fact]
    public async Task ReadRateLimitIncludesUnknownSlugs()
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        for (var i = 0; i < 120; i++) await client.GetAsync("/raw/abcdefgh");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/raw/abcdefgh")).StatusCode);
    }
    [Theory][InlineData("/")][InlineData("/health/live")][InlineData("/health/ready")][InlineData("/robots.txt")][InlineData("/app.css")][InlineData("/app.js")][InlineData("/swagger/v1/swagger.json")]
    public async Task InfrastructureRoutesRemainAccessible(string path)
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
    }
}
