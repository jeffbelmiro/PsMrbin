using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TempBin.Models;
using TempBin.Storage;
using TempBin.Services;
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
    [RedisFact]
    public async Task TrafficSizingIsAtomicSharedAndExpiresIdentities()
    {
        var (connection, store, prefix) = await Connect(); using var _ = connection;
        var db = connection.GetDatabase();
        RedisKey users = $"{prefix}{{traffic}}:publishers", volume = $"{prefix}{{traffic}}:creations";
        try
        {
            var lengths = await Task.WhenAll(Enumerable.Range(0, 100).Select(i => store.AllocateSlugLengthAsync($"publisher-{i}")));
            Assert.Equal(99, lengths.Count(length => length == 12));
            Assert.Single(lengths, length => length == 13);
            Assert.Equal(13, await store.AllocateSlugLengthAsync("publisher-0"));
            Assert.Equal(100, await db.SortedSetLengthAsync(users));
            var time = (RedisResult[])(await db.ExecuteAsync("TIME"))!;
            await db.SortedSetAddAsync(users, "stale", (long)time[0] - 301);
            await store.AllocateSlugLengthAsync("publisher-0");
            Assert.Null(await db.SortedSetScoreAsync(users, "stale"));
            Assert.InRange((await db.KeyTimeToLiveAsync(users))!.Value.TotalSeconds, 298, 300);
            Assert.InRange((await db.KeyTimeToLiveAsync(volume))!.Value.TotalSeconds, 89998, 90000);
        }
        finally { await db.KeyDeleteAsync([users, volume]); }
    }
    [RedisFact]
    public async Task TrafficVolumeUsesRecentBucketsAndNeverExceedsMaximumLength()
    {
        var (connection, store, prefix) = await Connect(); using var _ = connection;
        var db = connection.GetDatabase();
        RedisKey users = $"{prefix}{{traffic}}:publishers", volume = $"{prefix}{{traffic}}:creations";
        try
        {
            var time = (RedisResult[])(await db.ExecuteAsync("TIME"))!;
            var hour = (long)time[0] / 3600;
            await db.HashSetAsync(volume, [new HashEntry((hour - 25).ToString(), 1000000000000000L), new HashEntry(hour.ToString(), 1000000)]);
            Assert.Equal(AdaptiveSlugPolicy.LengthFor(1, 1000001), await store.AllocateSlugLengthAsync("publisher"));
            Assert.False(await db.HashExistsAsync(volume, (hour - 25).ToString()));
            await db.HashSetAsync(volume, hour.ToString(), 1000000000000000L);
            Assert.Equal(16, await store.AllocateSlugLengthAsync("publisher"));
        }
        finally { await db.KeyDeleteAsync([users, volume]); }
    }
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
