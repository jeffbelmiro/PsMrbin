using System.Text;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TempBin.Models;
using TempBin.Services;

namespace TempBin.Storage;

public interface IPasteStore
{
    Task<int> AllocateSlugLengthAsync(string owner);
    Task<Paste?> TryCreateAsync(string slug, string content, string owner = "", int ttlSeconds = PasteService.TtlSeconds);
    Task<PasteLookup> FindAsync(string slug);
    Task<DeleteState> DeleteAsync(string slug, string owner);
    Task<bool> IsHealthyAsync();
}

public sealed class TempBinOptions
{
    public string PublicOrigin { get; set; } = "http://localhost:8080";
    public string Redis { get; set; } = "localhost:6379,abortConnect=false";
    public string KeyPrefix { get; set; } = "tempbin:";
    public string? DeletionKey { get; set; }
    public string[] KnownProxies { get; set; } = [];
    public static bool ValidOrigin(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
}

// Redis TIME is authoritative: creation, expiry, and reads use the same clock.
// Both keys share a hash tag so the scripts also work on Redis Cluster.
public sealed class RedisPasteStore(IConnectionMultiplexer connection, IOptions<TempBinOptions> options) : IPasteStore
{
    private readonly IDatabase db = connection.GetDatabase();
    private RedisKey[] Keys(string slug) => [$"{options.Value.KeyPrefix}{{{slug}}}:data", $"{options.Value.KeyPrefix}{{{slug}}}:seen"];
    private const string TrafficScript = """
        local t = redis.call('TIME')
        local now = tonumber(t[1])
        local hour = math.floor(now / 3600)
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now - 300)
        redis.call('ZADD', KEYS[1], now, ARGV[1])
        redis.call('EXPIRE', KEYS[1], 300)
        redis.call('HINCRBY', KEYS[2], tostring(hour), 1)
        local fields = redis.call('HGETALL', KEYS[2])
        local creations = 0
        for i = 1, #fields, 2 do
            if tonumber(fields[i]) < hour - 24 then
                redis.call('HDEL', KEYS[2], fields[i])
            else
                creations = creations + tonumber(fields[i + 1])
            end
        end
        redis.call('EXPIRE', KEYS[2], 90000)
        return {redis.call('ZCARD', KEYS[1]), creations}
        """;

    public async Task<int> AllocateSlugLengthAsync(string owner)
    {
        var prefix = options.Value.KeyPrefix;
        var result = (RedisResult[])(await db.ScriptEvaluateAsync(TrafficScript,
            [$"{prefix}{{traffic}}:publishers", $"{prefix}{{traffic}}:creations"],
            [string.IsNullOrEmpty(owner) ? "unknown" : owner]))!;
        return AdaptiveSlugPolicy.LengthFor((long)result[0], (long)result[1]);
    }
    private const string CreateScript = """
        if redis.call('EXISTS', KEYS[1], KEYS[2]) > 0 then return nil end
        local t = redis.call('TIME')
        local now = t[1] * 1000 + math.floor(t[2] / 1000)
        redis.call('HSET', KEYS[1], 'content', ARGV[1], 'created', now, 'expires', now + tonumber(ARGV[4]), 'size', ARGV[2], 'version', 1, 'owner', ARGV[3])
        redis.call('PEXPIREAT', KEYS[1], now + tonumber(ARGV[4]))
        redis.call('SET', KEYS[2], '1', 'PXAT', now + math.max(86400000, tonumber(ARGV[4]) + 3600000))
        return {tostring(now), tostring(now + tonumber(ARGV[4]))}
        """;
    private const string ReadScript = """
        if redis.call('EXISTS', KEYS[1]) == 0 then
            if redis.call('EXISTS', KEYS[2]) == 1 then return {'expired'} end
            return {'missing'}
        end
        local t = redis.call('TIME')
        local now = t[1] * 1000 + math.floor(t[2] / 1000)
        local expires = redis.call('HGET', KEYS[1], 'expires')
        if tonumber(expires) <= now then redis.call('DEL', KEYS[1]); return {'expired'} end
        return {'found', redis.call('HGET', KEYS[1], 'content'), redis.call('HGET', KEYS[1], 'created'), expires,
            redis.call('HGET', KEYS[1], 'size'), redis.call('HGET', KEYS[1], 'version')}
        """;

    private const string DeleteScript = """
        if redis.call('EXISTS', KEYS[1]) == 0 then
            if redis.call('EXISTS', KEYS[2]) == 1 then return 'gone' end
            return 'missing'
        end
        local t = redis.call('TIME')
        local now = t[1] * 1000 + math.floor(t[2] / 1000)
        if tonumber(redis.call('HGET', KEYS[1], 'expires')) <= now then
            redis.call('DEL', KEYS[1]); return 'gone'
        end
        if ARGV[1] == '' or redis.call('HGET', KEYS[1], 'owner') ~= ARGV[1] then return 'forbidden' end
        if now >= tonumber(redis.call('HGET', KEYS[1], 'created')) + 600000 then return 'late' end
        redis.call('DEL', KEYS[1])
        return 'deleted'
        """;

    public async Task<Paste?> TryCreateAsync(string slug, string content, string owner = "", int ttlSeconds = PasteService.TtlSeconds)
    {
        PasteService.ValidateTtl(ttlSeconds);
        var size = Encoding.UTF8.GetByteCount(content);
        var result = await db.ScriptEvaluateAsync(CreateScript, Keys(slug), [content, size, owner, (long)ttlSeconds * 1000]);
        if (result.IsNull) return null;
        var values = (RedisResult[])result!;
        return new Paste(slug, content, DateTimeOffset.FromUnixTimeMilliseconds((long)values[0]),
            DateTimeOffset.FromUnixTimeMilliseconds((long)values[1]), size);
    }
    public async Task<PasteLookup> FindAsync(string slug)
    {
        var values = (RedisResult[])(await db.ScriptEvaluateAsync(ReadScript, Keys(slug)))!;
        var state = (string?)values[0];
        if (state != "found") return new(state == "expired" ? PasteState.Expired : PasteState.Missing);
        return new(PasteState.Found, new Paste(slug, (string)values[1]!, DateTimeOffset.FromUnixTimeMilliseconds((long)values[2]),
            DateTimeOffset.FromUnixTimeMilliseconds((long)values[3]), (int)values[4], (int)values[5]));
    }
    public async Task<bool> IsHealthyAsync() { await db.PingAsync(); return true; }
    public async Task<DeleteState> DeleteAsync(string slug, string owner)
    {
        var result = (string?)await db.ScriptEvaluateAsync(DeleteScript, Keys(slug), [owner]);
        return result switch
        {
            "deleted" => DeleteState.Deleted, "gone" => DeleteState.Gone, "missing" => DeleteState.Missing,
            "late" => DeleteState.TooLate, _ => DeleteState.Forbidden
        };
    }
}
