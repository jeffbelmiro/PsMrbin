using System.Collections.Concurrent;
using System.Text;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using TempBin.Models;
using TempBin.Storage;
using TempBin.Services;

namespace TempBin.Tests;

public sealed class ManualClock : TimeProvider
{
    private long ticks = DateTimeOffset.Parse("2026-11-08T12:00:00Z").Ticks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    public void Advance(TimeSpan delta) => Interlocked.Add(ref ticks, delta.Ticks);
}
public sealed class MemoryStore(ManualClock clock) : IPasteStore
{
    private readonly ConcurrentDictionary<string, (Paste? Paste, string Owner)> pastes = new();
    public int ActivePublishers { get; set; }
    private long recentCreations;
    public Task<int> AllocateSlugLengthAsync(string owner) => Task.FromResult(AdaptiveSlugPolicy.LengthFor(ActivePublishers, Interlocked.Increment(ref recentCreations)));
    public Task<Paste?> TryCreateAsync(string slug, string content, string owner = "")
    {
        var now = clock.GetUtcNow();
        var paste = new Paste(slug, content, now, now.AddMinutes(30), Encoding.UTF8.GetByteCount(content));
        return Task.FromResult(pastes.TryAdd(slug, (paste, owner)) ? paste : null);
    }
    public Task<PasteLookup> FindAsync(string slug)
    {
        if (!pastes.TryGetValue(slug, out var stored)) return Task.FromResult(new PasteLookup(PasteState.Missing));
        var paste = stored.Paste;
        if (paste is null) return Task.FromResult(new PasteLookup(PasteState.Expired));
        return Task.FromResult(clock.GetUtcNow() >= paste.ExpiresAt
            ? new PasteLookup(PasteState.Expired) : new PasteLookup(PasteState.Found, paste));
    }
    public Task<bool> IsHealthyAsync() => Task.FromResult(true);
    public Task<DeleteState> DeleteAsync(string slug, string owner)
    {
        if (!pastes.TryGetValue(slug, out var stored)) return Task.FromResult(DeleteState.Missing);
        if (stored.Paste is not { } paste || clock.GetUtcNow() >= paste.ExpiresAt) return Task.FromResult(DeleteState.Gone);
        if (string.IsNullOrEmpty(owner) || owner != stored.Owner) return Task.FromResult(DeleteState.Forbidden);
        if (clock.GetUtcNow() >= paste.CreatedAt.AddMinutes(10)) return Task.FromResult(DeleteState.TooLate);
        pastes[slug] = (null, "");
        return Task.FromResult(DeleteState.Deleted);
    }
}
public sealed class TestHost : WebApplicationFactory<Program>
{
    public ManualClock Clock { get; } = new();
    public string[] KnownProxies { get; set; } = [];
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development")
        .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(KnownProxies.Select((ip, index) =>
            new KeyValuePair<string, string?>($"TempBin:KnownProxies:{index}", ip))))
        .ConfigureServices(services =>
    {
        services.RemoveAll<IPasteStore>();
        services.AddSingleton<IPasteStore>(new MemoryStore(Clock));
        services.AddSingleton<IStartupFilter, TestIpFilter>();
    });
}
public sealed class TestIpFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers["X-Test-IP"].FirstOrDefault() ?? "127.0.0.1");
            return nextMiddleware();
        });
        next(app);
    };
}
