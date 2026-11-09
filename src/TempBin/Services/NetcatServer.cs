using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TempBin.Models;
using TempBin.Storage;
using TempBin.Localization;

namespace TempBin.Services;

public sealed class NetcatOptions
{
    public bool Enabled { get; set; }
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 9999;
    public string? PublicHost { get; set; }
    public int PublicPort { get; set; } = 9999;
    public int IdleTimeoutMilliseconds { get; set; } = 2000;
    public int TotalTimeoutSeconds { get; set; } = 15;
}

// Raw UTF-8 in, one raw URL plus newline out. No HTTP headers or client library required.
public sealed class NetcatServer(IServiceProvider services, IOptions<NetcatOptions> settings,
    IOptions<TempBinOptions> options, ClientIdentity identity, ILogger<NetcatServer> logger) : BackgroundService
{
    private TcpListener? listener;
    public int BoundPort => listener is null ? 0 : ((IPEndPoint)listener.LocalEndpoint).Port;
    private readonly ConcurrentDictionary<Task, byte> pending = new();
    private readonly ConcurrencyLimiter concurrency = new(new() { PermitLimit = 32, QueueLimit = 0 });
    private readonly FixedWindowRateLimiter global = new(new() { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true });
    private readonly PartitionedRateLimiter<IPAddress> perIp = PartitionedRateLimiter.Create<IPAddress, IPAddress>(ip =>
        RateLimitPartition.GetFixedWindowLimiter(ip, _ => new() { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Value.Enabled) return;
        listener = new TcpListener(IPAddress.Parse(settings.Value.ListenAddress), settings.Value.Port);
        listener.Start(64);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                var task = HandleAsync(client, stoppingToken);
                pending.TryAdd(task, 0);
                _ = task.ContinueWith(completed => pending.TryRemove(completed, out var unused),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            listener.Stop();
            await Task.WhenAll(pending.Keys);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken stop)
    {
        using (client)
        using (var total = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            total.CancelAfter(TimeSpan.FromSeconds(settings.Value.TotalTimeoutSeconds));
            var stream = client.GetStream();
            try
            {
                var ip = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.MapToIPv6();
                using var capacity = concurrency.AttemptAcquire();
                using var volume = global.AttemptAcquire();
                using var personal = perIp.AttemptAcquire(ip);
                if (!capacity.IsAcquired || !volume.IsAcquired || !personal.IsAcquired)
                {
                    await ReplyAsync(stream, "ERROR 429: too many requests; try again in a minute.", total.Token);
                    return;
                }
                using var content = new MemoryStream();
                var bytes = new byte[8192];
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
                    idle.CancelAfter(settings.Value.IdleTimeoutMilliseconds);
                    int count;
                    try { count = await stream.ReadAsync(bytes, idle.Token); }
                    catch (OperationCanceledException) when (!total.IsCancellationRequested && content.Length > 0) { break; }
                    if (count == 0) break; // nc -N, nc -q or shutdown(SHUT_WR)
                    if (content.Length + count > PasteService.MaxBytes)
                        throw new PasteValidationException("error.size", 413);
                    content.Write(bytes, 0, count);
                }
                total.Token.ThrowIfCancellationRequested();
                var text = new UTF8Encoding(false, true).GetString(content.GetBuffer(), 0, (int)content.Length);
                var paste = await services.GetRequiredService<PasteService>().CreateAsync(text, identity.Fingerprint(ip));
                await ReplyAsync(stream, $"{options.Value.PublicOrigin.TrimEnd('/')}/raw/{paste.Slug}", total.Token);
            }
            catch (PasteValidationException ex) { await SafeErrorAsync(stream, $"ERROR {ex.Status}: {Text.Get(ex.Message, "en")}", total.Token); }
            catch (DecoderFallbackException) { await SafeErrorAsync(stream, "ERROR 400: send valid UTF-8 text only.", total.Token); }
            catch (OperationCanceledException) { await SafeErrorAsync(stream, "ERROR 408: upload timed out.", stop); }
            catch (RedisException) { await SafeErrorAsync(stream, "ERROR 503: storage is temporarily unavailable.", total.Token); }
            catch (IOException) { }
            catch (SocketException) { }
            catch (Exception ex)
            {
                logger.LogWarning("Falha no pedido TCP ({ExceptionType}).", ex.GetType().Name);
                await SafeErrorAsync(stream, "ERROR 500: the request could not be processed.", total.Token);
            }
        }
    }
    private static ValueTask ReplyAsync(NetworkStream stream, string text, CancellationToken token) => stream.WriteAsync(Encoding.UTF8.GetBytes(text + "\n"), token);
    private static async Task SafeErrorAsync(NetworkStream stream, string text, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(1));
        try { await ReplyAsync(stream, text, limit.Token); }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
    }
    public override void Dispose()
    {
        concurrency.Dispose(); global.Dispose(); perIp.Dispose(); base.Dispose();
    }
}
