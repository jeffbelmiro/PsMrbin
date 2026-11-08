using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TempBin.Models;
using TempBin.Storage;
using Xunit;

namespace TempBin.Tests;

public sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEMPBIN_TEST_REDIS")))
            Skip = "Defina TEMPBIN_TEST_REDIS para executar contra Redis real (obrigatório no CI).";
    }
}

public class RedisTests
{
    private static async Task<(ConnectionMultiplexer, RedisPasteStore, string)> Connect()
    {
        var connection = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("TEMPBIN_TEST_REDIS")!);
        // Every test uses a distinct prefix; never flush a shared Redis database.
        var prefix = $"tempbin-test:{Guid.NewGuid():N}:";
        return (connection, new RedisPasteStore(connection, Options.Create(new TempBinOptions { KeyPrefix = prefix })), prefix);
    }
    private static RedisKey Data(string prefix, string slug) => $"{prefix}{{{slug}}}:data";
    private static RedisKey Seen(string prefix, string slug) => $"{prefix}{{{slug}}}:seen";

    [RedisFact]
    public async Task AtomicCreationHasExactTtlAndNoReadRenewal()
    {
        var (connection, store, prefix) = await Connect(); using var _ = connection;
        const string slug = "abcdefgh"; var db = connection.GetDatabase();
        try
        {
            var paste = await store.TryCreateAsync(slug, "Olá 🌍"); Assert.NotNull(paste);
            Assert.Equal(TimeSpan.FromMinutes(30), paste!.ExpiresAt - paste.CreatedAt);
            var before = await db.KeyTimeToLiveAsync(Data(prefix, slug));
            Assert.InRange(before!.Value.TotalSeconds, 1798, 1800);
            var result = await store.FindAsync(slug);
            Assert.Equal(paste, result.Paste);
            var after = await db.KeyTimeToLiveAsync(Data(prefix, slug)); Assert.True(after <= before);
            Assert.Equal("1", (string?)await db.StringGetAsync(Seen(prefix, slug)));
            Assert.InRange((await db.KeyTimeToLiveAsync(Seen(prefix, slug)))!.Value.TotalSeconds, 86398, 86400);
        }
        finally { await db.KeyDeleteAsync([Data(prefix, slug), Seen(prefix, slug)]); }
    }
    [RedisFact]
    public async Task ConcurrentCollisionHasExactlyOneWinner()
    {
        var (connection, store, prefix) = await Connect(); using var _ = connection;
        const string slug = "jkmnpqrs"; var db = connection.GetDatabase();
        try
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(i => store.TryCreateAsync(slug, $"paste-{i}")));
            var winner = Assert.Single(results, p => p is not null);
            Assert.Equal(winner, (await store.FindAsync(slug)).Paste);
        }
        finally { await db.KeyDeleteAsync([Data(prefix, slug), Seen(prefix, slug)]); }
    }
    [RedisFact]
    public async Task ExpiredTimestampIsRejectedEvenBeforeRedisKeyRemoval()
    {
        var (connection, store, prefix) = await Connect(); using var _ = connection;
        const string slug = "tuvwxyz2"; var db = connection.GetDatabase();
        try
        {
            await store.TryCreateAsync(slug, "must disappear");
            await db.HashSetAsync(Data(prefix, slug), "expires", 1);
            var result = await store.FindAsync(slug);
            Assert.Equal(PasteState.Expired, result.State); Assert.Null(result.Paste);
            Assert.False(await db.KeyExistsAsync(Data(prefix, slug)));
            Assert.Null(await store.TryCreateAsync(slug, "cannot reuse"));
        }
        finally { await db.KeyDeleteAsync([Data(prefix, slug), Seen(prefix, slug)]); }
    }
    [RedisFact]
    public async Task RedisTtlDeletesContentAndMarkerEventuallyBecomes404()
    {
        var (connection, store, prefix) = await Connect(); using var _ = connection;
        const string slug = "23456789"; var db = connection.GetDatabase();
        try
        {
            await store.TryCreateAsync(slug, "temporary");
            // Shorten only the test key's TTL; production expiry remains hardcoded to 1800 seconds.
            await db.KeyExpireAsync(Data(prefix, slug), TimeSpan.FromMilliseconds(5));
            await Task.Delay(40);
            Assert.False(await db.KeyExistsAsync(Data(prefix, slug)));
            Assert.Equal(PasteState.Expired, (await store.FindAsync(slug)).State);
            Assert.Null(await store.TryCreateAsync(slug, "cannot reuse"));
            await db.KeyDeleteAsync(Seen(prefix, slug));
            Assert.Equal(PasteState.Missing, (await store.FindAsync(slug)).State);
        }
        finally { await db.KeyDeleteAsync([Data(prefix, slug), Seen(prefix, slug)]); }
    }
    [RedisFact]
    public async Task DeleteChecksOwnerAndDeadlineAtomicallyAndKeepsTombstone()
    {
        var (connection, store, prefix) = await Connect(); using var _ = connection;
        const string slug = "abcdefgh"; var db = connection.GetDatabase();
        try
        {
            var paste = await store.TryCreateAsync(slug, "remove me", "owner-fingerprint"); Assert.NotNull(paste);
            Assert.Equal(DeleteState.Forbidden, await store.DeleteAsync(slug, "wrong-owner"));
            Assert.Equal(DeleteState.Forbidden, await store.DeleteAsync(slug, ""));
            Assert.Equal(PasteState.Found, (await store.FindAsync(slug)).State);
            // Move the creation timestamp to the exact ten-minute boundary without waiting.
            await db.HashSetAsync(Data(prefix, slug), "created", paste!.CreatedAt.AddMinutes(-10).ToUnixTimeMilliseconds());
            Assert.Equal(DeleteState.TooLate, await store.DeleteAsync(slug, "owner-fingerprint"));
            await db.HashSetAsync(Data(prefix, slug), "created", paste.CreatedAt.ToUnixTimeMilliseconds());
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => store.DeleteAsync(slug, "owner-fingerprint")));
            Assert.Single(results, state => state == DeleteState.Deleted);
            Assert.Equal(7, results.Count(state => state == DeleteState.Gone));
            Assert.False(await db.KeyExistsAsync(Data(prefix, slug)));
            Assert.True(await db.KeyExistsAsync(Seen(prefix, slug)));
            Assert.Equal(PasteState.Expired, (await store.FindAsync(slug)).State);
            Assert.Null(await store.TryCreateAsync(slug, "no immediate reuse"));
        }
        finally { await db.KeyDeleteAsync([Data(prefix, slug), Seen(prefix, slug)]); }
    }
    [RedisFact]
    public async Task DeleteRespectsExpiryAndLegacyPastesHaveNoOwner()
    {
        var (connection, store, prefix) = await Connect(); using var _ = connection;
        const string slug = "abcdefgh"; var db = connection.GetDatabase();
        try
        {
            Assert.Equal(DeleteState.Missing, await store.DeleteAsync(slug, "owner"));
            await store.TryCreateAsync(slug, "old paste");
            Assert.Equal(DeleteState.Forbidden, await store.DeleteAsync(slug, "owner"));
            await db.HashSetAsync(Data(prefix, slug), "expires", 1);
            Assert.Equal(DeleteState.Gone, await store.DeleteAsync(slug, "owner"));
            Assert.False(await db.KeyExistsAsync(Data(prefix, slug)));
        }
        finally { await db.KeyDeleteAsync([Data(prefix, slug), Seen(prefix, slug)]); }
    }
}
