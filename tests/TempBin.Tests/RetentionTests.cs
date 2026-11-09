using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TempBin.Tests;

public class RetentionTests
{
    [Theory]
    [InlineData(1800)][InlineData(28800)][InlineData(43200)][InlineData(86400)]
    public async Task BothInputFormatsUseSelectedDeadlineAndNeverRenew(int seconds)
    {
        foreach (var plain in new[] { false, true })
        {
            await using var app = new TestHost(); using var client = app.CreateClient();
            var response = plain
                ? await client.PostAsync($"/api/v1/pastes?ttl_seconds={seconds}", new StringContent("synthetic", Encoding.UTF8, "text/plain"))
                : await client.PostAsJsonAsync("/api/v1/pastes", new { content = "synthetic", ttlSeconds = seconds });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(seconds, result.GetProperty("ttl_seconds").GetInt32());
            Assert.Equal(TimeSpan.FromSeconds(seconds), result.GetProperty("expires_at").GetDateTimeOffset() - result.GetProperty("created_at").GetDateTimeOffset());
            var slug = result.GetProperty("slug").GetString();
            app.Clock.Advance(TimeSpan.FromSeconds(seconds - 1));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/raw/{slug}")).StatusCode);
            app.Clock.Advance(TimeSpan.FromSeconds(1));
            foreach (var path in new[] { $"/raw/{slug}", $"/{slug}", $"/download/{slug}", $"/api/v1/pastes/{slug}" })
                Assert.Equal(HttpStatusCode.Gone, (await client.GetAsync(path)).StatusCode);
        }
    }
    [Theory]
    [InlineData(0)][InlineData(-1)][InlineData(3600)][InlineData(86401)][InlineData(int.MaxValue)]
    public async Task UnsupportedDurationsAreRejected(int seconds)
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/pastes", new { content = "synthetic", ttlSeconds = seconds })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/v1/pastes?ttl_seconds={seconds}", new StringContent("synthetic", Encoding.UTF8, "text/plain"))).StatusCode);
    }
    [Theory][InlineData("abc")][InlineData("1800&ttl_seconds=86400")][InlineData("")]
    public async Task MalformedPlainTextDurationIsRejected(string value)
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/v1/pastes?ttl_seconds={value}", new StringContent("synthetic", Encoding.UTF8, "text/plain"))).StatusCode);
    }
}
