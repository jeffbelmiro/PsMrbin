using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TempBin.Localization;
using TempBin.Services;
using TempBin.Web;
using Xunit;

namespace TempBin.Tests;

public class AdaptiveFeaturesTests
{
    [Theory][InlineData(0,12)][InlineData(99,12)][InlineData(100,13)][InlineData(999,13)][InlineData(1000,14)]
    [InlineData(10000,15)][InlineData(100000,16)][InlineData(long.MaxValue,16)]
    public void ActivityThresholdsIncreaseLength(long publishers, int length) => Assert.Equal(length, AdaptiveSlugPolicy.LengthFor(publishers, 1));

    [Fact]
    public void VolumeIncreasesLengthIndependentlyOfIdentityCount()
    {
        Assert.True(AdaptiveSlugPolicy.LengthFor(1, 1_000_000) > 12);
        Assert.Equal(16, AdaptiveSlugPolicy.LengthFor(1, long.MaxValue));
    }
    [Fact]
    public async Task ServiceUsesStoreActivityAndStillReadsLegacyLinks()
    {
        var store = new MemoryStore(new ManualClock()) { ActivePublishers = 1000 };
        await store.TryCreateAsync("abcdefgh", "legacy");
        var service = new PasteService(store, new SlugGenerator());
        Assert.Equal(14, (await service.CreateAsync("adaptive")).Slug.Length);
        Assert.Equal("legacy", (await service.FindAsync("abcdefgh")).Paste!.Content);
    }
    [Fact]
    public async Task UiAndOpenApiDoNotOfferManualLength()
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        var html = await client.GetStringAsync("/");
        Assert.DoesNotContain("id=\"slug-length\"", html);
        Assert.DoesNotContain("slug_length", await client.GetStringAsync("/swagger/v1/swagger.json"));
        Assert.Contains("12–16", WebUtility.HtmlDecode(html));
    }
    [Theory][InlineData("en")][InlineData("es")][InlineData("pt-BR")][InlineData("fr")][InlineData("zh-CN")]
    public async Task DisclaimerAndFooterAreLocalizedAndBuildDateIsFixed(string locale)
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        var response = await client.GetAsync($"/disclaimer?lang={locale}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains(Text.Get("disclaimer.title", locale), html);
        Assert.Contains(Text.Get("disclaimer.rights", locale), html);
        Assert.Contains($"App v{BuildInfo.Version}", html);
        Assert.Contains(BuildInfo.BuiltAt.ToString("O", CultureInfo.InvariantCulture), html);
        Assert.Contains("href=\"/disclaimer\"", await client.GetStringAsync("/"));
        Assert.Matches(@"^\d+\.\d+\.\d+", BuildInfo.Version);
        Assert.Equal(TimeSpan.Zero, BuildInfo.BuiltAt.Offset);
        Assert.InRange(BuildInfo.BuiltAt, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), DateTimeOffset.UtcNow.AddMinutes(1));
    }
    [Fact]
    public async Task ResultExamplesUseRealRawUrlAndNeverInterpolateSavedTextAsCode()
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        var created = await client.PostAsJsonAsync("/api/v1/pastes", new { content = "$(touch should-not-exist)\n\"quoted\" 中文" });
        var result = await created.Content.ReadFromJsonAsync<JsonElement>();
        var slug = result.GetProperty("slug").GetString();
        var html = await client.GetStringAsync($"/{slug}");
        foreach (var id in new[] { "save-file", "post-text", "post-json", "post-field" })
        {
            var command = WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(html, $"<code id=\"{id}\">(.*?)</code>", System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value);
            Assert.Contains($"http://localhost:8080/raw/{slug}", command);
            Assert.DoesNotContain("touch should-not-exist", command);
            Assert.Contains("curl --fail", command);
        }
        Assert.Contains("--rawfile content", html);
        Assert.Contains("length == 1", html);
        Assert.Contains("api.example.com", html);
    }
}
