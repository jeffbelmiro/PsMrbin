using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TempBin.Services;
using TempBin.Storage;
using Xunit;

namespace TempBin.Tests;

public class TermbinApiTests
{
    private static async Task<string> Create(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/pastes", new { content = "terminal output\n<script>plain text</script>" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
    }
    [Theory][InlineData("curl/8.0", null)][InlineData("Wget/1.21", null)][InlineData("test", "text/plain")]
    public async Task MainLinkServesRawTextToTerminalClients(string agent, string? accept)
    {
        await using var app = new TestHost(); using var client = app.CreateClient(); var slug = await Create(client);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(agent);
        if (accept is not null) client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        var response = await client.GetAsync($"/{slug}");
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("terminal output\n<script>plain text</script>", await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl!.NoStore);
    }
    [Fact]
    public async Task ExplicitHtmlKeepsResultPageEvenWithCurlAgent()
    {
        await using var app = new TestHost(); using var client = app.CreateClient(); var slug = await Create(client);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("curl/8.0"); client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        var response = await client.GetAsync($"/{slug}");
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("Line numbers", await response.Content.ReadAsStringAsync());
    }
    [Theory][InlineData(true)][InlineData(false)]
    public async Task SameIpCanDeleteViaRestOrTermbinCompatibleEndpoint(bool rest)
    {
        await using var app = new TestHost(); using var client = app.CreateClient(); var slug = await Create(client);
        var response = rest ? await client.DeleteAsync($"/api/v1/pastes/{slug}") : await client.PostAsync($"/delete?slug={slug}", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await client.GetAsync($"/raw/{slug}")).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await client.DeleteAsync($"/api/v1/pastes/{slug}")).StatusCode);
    }
    [Fact]
    public async Task OtherIpCannotDelete()
    {
        await using var app = new TestHost(); using var client = app.CreateClient(); var slug = await Create(client);
        client.DefaultRequestHeaders.Add("X-Test-IP", "192.0.2.2");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/v1/pastes/{slug}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/raw/{slug}")).StatusCode);
    }
    [Fact]
    public async Task DeleteWindowEndsAtExactlyTenMinutes()
    {
        await using var app = new TestHost(); using var client = app.CreateClient(); var slug = await Create(client);
        app.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/v1/pastes/{slug}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/raw/{slug}")).StatusCode);
    }
    [Fact]
    public async Task DeleteJustBeforeTenMinutesIsAllowed()
    {
        await using var app = new TestHost(); using var client = app.CreateClient(); var slug = await Create(client);
        app.Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromMilliseconds(1));
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/pastes/{slug}")).StatusCode);
    }
    [Theory][InlineData("Origin", "https://attacker.example")][InlineData("Sec-Fetch-Site", "cross-site")]
    public async Task CrossOriginDeletionIsRejected(string header, string value)
    {
        await using var app = new TestHost(); using var client = app.CreateClient(); var slug = await Create(client);
        client.DefaultRequestHeaders.Add(header, value);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/delete?slug={slug}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/raw/{slug}")).StatusCode);
    }
    [Fact]
    public async Task DeleteDistinguishesMissingAndExpired()
    {
        await using var app = new TestHost(); using var client = app.CreateClient(); var slug = await Create(client);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/v1/pastes/abcdefgh")).StatusCode);
        app.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(HttpStatusCode.Gone, (await client.DeleteAsync($"/api/v1/pastes/{slug}")).StatusCode);
    }
    [Fact]
    public void IpFingerprintsAreKeyedAndNormalizeMappedAddresses()
    {
        var identity = new ClientIdentity(Options.Create(new TempBinOptions()));
        var sameIp = IPAddress.Parse("192.0.2.1");
        Assert.Equal(identity.Fingerprint(sameIp), identity.Fingerprint(sameIp.MapToIPv6()));
        Assert.NotEqual(identity.Fingerprint(sameIp), identity.Fingerprint(IPAddress.Parse("192.0.2.2")));
        Assert.NotEqual(identity.Fingerprint(sameIp), new ClientIdentity(Options.Create(new TempBinOptions())).Fingerprint(sameIp));
        Assert.Equal("", identity.Fingerprint(null));
    }
    [Fact]
    public async Task TrustedProxyUsesForwardedIpForDeletion()
    {
        await using var app = new TestHost { KnownProxies = ["127.0.0.1"] }; using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "192.0.2.1");
        var slug = await Create(client);
        client.DefaultRequestHeaders.Remove("X-Forwarded-For"); client.DefaultRequestHeaders.Add("X-Forwarded-For", "192.0.2.2");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/v1/pastes/{slug}")).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Forwarded-For"); client.DefaultRequestHeaders.Add("X-Forwarded-For", "192.0.2.1");
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/pastes/{slug}")).StatusCode);
    }
    [Fact]
    public async Task ForwardedIpFromUntrustedPeerCannotImpersonateCreator()
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        var slug = await Create(client);
        client.DefaultRequestHeaders.Add("X-Test-IP", "192.0.2.2");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "127.0.0.1");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/v1/pastes/{slug}")).StatusCode);
    }
}
