using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TempBin.Models;
using TempBin.Storage;

namespace TempBin.Tests;

public sealed class ManualClock : TimeProvider
{
    private long ticks = DateTimeOffset.Parse("2026-11-08T12:00:00Z").Ticks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    public void Advance(TimeSpan delta) => Interlocked.Add(ref ticks, delta.Ticks);
}
public sealed class MemoryStore(ManualClock clock) : IPasteStore
{
    private readonly ConcurrentDictionary<string, Paste> pastes = new();
    public Task<Paste?> TryCreateAsync(string slug, string content)
    {
        var now = clock.GetUtcNow();
        var paste = new Paste(slug, content, now, now.AddMinutes(30), Encoding.UTF8.GetByteCount(content));
        return Task.FromResult(pastes.TryAdd(slug, paste) ? paste : null);
    }
    public Task<PasteLookup> FindAsync(string slug)
    {
        if (!pastes.TryGetValue(slug, out var paste)) return Task.FromResult(new PasteLookup(PasteState.Missing));
        return Task.FromResult(clock.GetUtcNow() >= paste.ExpiresAt
            ? new PasteLookup(PasteState.Expired) : new PasteLookup(PasteState.Found, paste));
    }
    public Task<bool> IsHealthyAsync() => Task.FromResult(true);
}
public sealed class TestHost : WebApplicationFactory<Program>
{
    public ManualClock Clock { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development").ConfigureServices(services =>
    {
        services.RemoveAll<IPasteStore>();
        services.AddSingleton<IPasteStore>(new MemoryStore(Clock));
    });
}
