using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TempBin.Localization;
using Xunit;

namespace TempBin.Tests;

public class LocalizationTests
{
    [Fact]
    public void EveryCatalogEntryHasFiveNonEmptyTranslationsAndValidPlaceholders()
    {
        Assert.NotEmpty(Text.Catalog);
        foreach (var entry in Text.Catalog)
        {
            Assert.Equal(Text.Cultures.Length, entry.Value.Length);
            Assert.All(entry.Value, value =>
            {
                Assert.False(string.IsNullOrWhiteSpace(value), entry.Key);
                Assert.DoesNotContain('—', value);
                _ = string.Format(CultureInfo.InvariantCulture, value, 2, 15);
            });
            var expected = System.Text.RegularExpressions.Regex.Matches(entry.Value[0], @"\{\d+\}").Select(m => m.Value).Order().ToArray();
            foreach (var value in entry.Value)
                Assert.Equal(expected, System.Text.RegularExpressions.Regex.Matches(value, @"\{\d+\}").Select(m => m.Value).Order().ToArray());
        }
    }
    [Theory][InlineData("en", "Share text for 30 minutes.")][InlineData("es", "Comparte texto durante 30 minutos.")]
    [InlineData("pt-BR", "Compartilhe texto por 30 minutos.")][InlineData("fr", "Partagez du texte pendant 30 minutes.")][InlineData("zh-CN", "分享文本，保留 30 分钟。")]
    public async Task HomepageIsRenderedInEachLanguage(string locale, string headline)
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        var response = await client.GetAsync($"/?lang={locale}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains($"<html lang=\"{locale}\">", html);
        Assert.Contains(headline, html);
        Assert.Contains("<span class=\"logo\" aria-hidden=\"true\">t<span>↗</span></span>", html);
        Assert.Contains("Timebin", html);
        Assert.Equal(locale, response.Content.Headers.ContentLanguage.Single());
        Assert.Contains("HttpOnly", string.Join("", response.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task FirstVisitUsesEnglishRegardlessOfBrowserLanguage()
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pt-BR,fr;q=0.8");
        var response = await client.GetAsync("/");
        Assert.Contains("<html lang=\"en\">", await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }
    [Fact]
    public async Task LanguageChoicePersistsAndExplicitChoiceOverridesIt()
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        await client.GetAsync("/?lang=fr");
        Assert.Contains("<html lang=\"fr\">", await client.GetStringAsync("/"));
        await client.GetAsync("/?lang=es");
        Assert.Contains("<html lang=\"es\">", await client.GetStringAsync("/"));
        await client.GetAsync("/?lang=en");
        Assert.Contains("<html lang=\"en\">", await client.GetStringAsync("/"));
    }
    [Theory][InlineData("es-MX", "es")][InlineData("pt", "pt-BR")][InlineData("zh", "zh-CN")][InlineData("de", "en")][InlineData("%3Cscript%3E", "en")]
    public async Task RegionalVariantsAndUnsupportedLanguagesHaveSafeFallbacks(string requested, string expected)
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        Assert.Contains($"<html lang=\"{expected}\">", await client.GetStringAsync($"/?lang={requested}"));
    }
    [Theory][InlineData("en")][InlineData("es")][InlineData("pt-BR")][InlineData("fr")][InlineData("zh-CN")]
    public async Task ValidationErrorsAreLocalizedWithoutChangingStatusCodes(string locale)
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync($"/api/v1/pastes?lang={locale}", new { content = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Text.Get("error.empty", locale), json.GetProperty("error").GetString());
        Assert.Equal(locale, response.Content.Headers.ContentLanguage.Single());
    }
    [Fact]
    public async Task ApiAcceptLanguageUsesQualityAndSkipsUnsupportedLanguages()
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("de;q=1,fr;q=0.9,es;q=0.7");
        var response = await client.GetAsync("/api/v1/pastes/abcdefgh");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Text.Get("error.missing", "fr"), json.GetProperty("error").GetString());
    }
    [Fact]
    public async Task LanguagesNeverChangeUserContentOrApiFieldNames()
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        const string content = "English\nPortuguês 🧪\r\n中文\n<script>not code</script>";
        var created = await client.PostAsJsonAsync("/api/v1/pastes?lang=zh", new { content });
        var json = await created.Content.ReadFromJsonAsync<JsonElement>(); var slug = json.GetProperty("slug").GetString();
        Assert.True(json.TryGetProperty("expires_at", out _));
        foreach (var locale in Text.Cultures)
        {
            Assert.Equal(content, await client.GetStringAsync($"/raw/{slug}?lang={locale}"));
            Assert.Equal(Encoding.UTF8.GetBytes(content), await client.GetByteArrayAsync($"/download/{slug}?lang={locale}"));
            var html = await client.GetStringAsync($"/{slug}?lang={locale}");
            Assert.Contains($"<html lang=\"{locale}\">", html);
            Assert.Contains("&lt;script&gt;", html);
            Assert.DoesNotContain("<script>not code</script>", html);
        }
    }
    [Fact]
    public async Task ConcurrentRequestsKeepTheirOwnLanguage()
    {
        await using var app = new TestHost();
        var responses = await Task.WhenAll(Text.Cultures.Select(async locale =>
        {
            using var client = app.CreateClient();
            var response = await client.GetAsync($"/api/v1/pastes/abcdefgh?lang={locale}");
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            return (locale, error: json.GetProperty("error").GetString());
        }));
        foreach (var response in responses) Assert.Equal(Text.Get("error.missing", response.locale), response.error);
    }
}
