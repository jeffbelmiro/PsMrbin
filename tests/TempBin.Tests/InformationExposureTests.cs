using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;
using TempBin.Models;
using TempBin.Storage;
using Xunit;

namespace TempBin.Tests;

public class InformationExposureTests
{
    private sealed class UnavailableStore : IPasteStore
    {
        // An artificial error marker verifies that exception details never reach HTTP clients.
        public const string Canary = "internal-only-error-marker-for-test";
        private static RedisConnectionException Failure() => new(ConnectionFailureType.UnableToConnect, Canary);
        public Task<int> AllocateSlugLengthAsync(string owner) => throw Failure();
        public Task<Paste?> TryCreateAsync(string slug, string content, string owner = "") => throw Failure();
        public Task<PasteLookup> FindAsync(string slug) => throw Failure();
        public Task<DeleteState> DeleteAsync(string slug, string owner) => throw Failure();
        public Task<bool> IsHealthyAsync() => throw Failure();
    }
    private sealed class Host : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPasteStore>();
            services.AddSingleton<IPasteStore, UnavailableStore>();
        });
    }
    [Theory][InlineData("/raw/abcdefgh")][InlineData("/health/ready")]
    public async Task StorageFailuresAreSanitizedAndKeepAllSecurityHeaders(string path)
    {
        await using var app = new Host(); using var client = app.CreateClient();
        var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain(UnavailableStore.Canary, body);
        Assert.DoesNotContain("RedisConnectionException", body);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("noindex", response.Headers.GetValues("X-Robots-Tag").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.True(JsonDocument.Parse(body).RootElement.TryGetProperty("error", out _));
    }
    [Fact]
    public async Task ValidationFailuresNeverEchoSubmittedText()
    {
        await using var app = new TestHost(); using var client = app.CreateClient();
        const string marker = "untrusted-input-marker";
        var response = await client.PostAsync("/api/v1/pastes", new StringContent("{broken:" + marker, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(marker, await response.Content.ReadAsStringAsync());
        Assert.Contains("script-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
    }
}
