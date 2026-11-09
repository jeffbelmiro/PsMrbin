using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TempBin.Services;
using TempBin.Storage;
using Xunit;

namespace TempBin.Tests;

public class TerminalTests
{
    private sealed class Server : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        public MemoryStore Store { get; } = new(new ManualClock());
        public NetcatServer Listener { get; }
        public Server(int idle = 100, int total = 3)
        {
            provider = new ServiceCollection().AddSingleton<IPasteStore>(Store).AddSingleton<ISlugGenerator, SlugGenerator>()
                .AddSingleton<PasteService>().BuildServiceProvider();
            var options = Options.Create(new TempBinOptions { PublicOrigin = "https://bin.example" });
            Listener = new NetcatServer(provider, Options.Create(new NetcatOptions { Enabled = true, ListenAddress = "127.0.0.1", Port = 0,
                IdleTimeoutMilliseconds = idle, TotalTimeoutSeconds = total }), options, new ClientIdentity(options), NullLogger<NetcatServer>.Instance);
        }
        public Task StartAsync() => Listener.StartAsync(CancellationToken.None);
        public async Task<string> SendAsync(byte[] bytes, bool halfClose = true)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, Listener.BoundPort);
            var stream = client.GetStream();
            await stream.WriteAsync(bytes);
            if (halfClose) client.Client.Shutdown(SocketShutdown.Send);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(timeout.Token);
        }
        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Listener.StopAsync(timeout.Token); Listener.Dispose(); await provider.DisposeAsync();
        }
    }
    [Fact]
    public async Task SocketUploadPreservesUtf8AndReturnsOnlyRawUrl()
    {
        await using var server = new Server(); await server.StartAsync();
        const string text = "Olá 🌍\r\nlinha dois\n";
        var result = await server.SendAsync(Encoding.UTF8.GetBytes(text));
        Assert.Matches("^https://bin.example/raw/[a-z2-9]{12}\\n$", result);
        var slug = result.Trim().Split('/').Last();
        Assert.Equal(text, (await server.Store.FindAsync(slug)).Paste!.Content);
    }
    [Fact]
    public async Task PlainNetcatDoesNotNeedToHalfCloseConnection()
    {
        await using var server = new Server(); await server.StartAsync();
        var result = await server.SendAsync(Encoding.UTF8.GetBytes("echo output\n"), halfClose: false);
        Assert.StartsWith("https://bin.example/raw/", result);
    }
    [Fact]
    public async Task DelayedChunksAreCombinedUntilEndOfInput()
    {
        await using var server = new Server(idle: 500); await server.StartAsync();
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, server.Listener.BoundPort);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("first"));
        await Task.Delay(20);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("second"));
        client.Client.Shutdown(SocketShutdown.Send);
        using var reader = new StreamReader(stream);
        var url = await reader.ReadToEndAsync();
        Assert.Equal("firstsecond", (await server.Store.FindAsync(url.Trim().Split('/').Last())).Paste!.Content);
    }
    [Fact]
    public async Task EmptyAndInvalidUtf8AreRejected()
    {
        await using var server = new Server(); await server.StartAsync();
        Assert.StartsWith("ERROR 400:", await server.SendAsync([]));
        Assert.StartsWith("ERROR 400:", await server.SendAsync([0xff, 0xfe]));
        Assert.StartsWith("ERROR 400:", await server.SendAsync(Encoding.UTF8.GetBytes("  \n")));
    }
    [Fact]
    public async Task Utf8ByteLimitIsEnforcedOnTcp()
    {
        await using var server = new Server(); await server.StartAsync();
        Assert.StartsWith("https://bin.example/raw/", await server.SendAsync(Encoding.UTF8.GetBytes(new string('á', 131072))));
        Assert.StartsWith("ERROR 413:", await server.SendAsync(new byte[262145]));
    }
    [Fact]
    public async Task SilentConnectionTimesOutWithoutCreatingPaste()
    {
        await using var server = new Server(); await server.StartAsync();
        Assert.StartsWith("ERROR 408:", await server.SendAsync([], halfClose: false));
    }
    [Fact]
    public async Task TotalTimeoutNeverPublishesPartialInput()
    {
        await using var server = new Server(idle: 2000, total: 1); await server.StartAsync();
        Assert.StartsWith("ERROR 408:", await server.SendAsync(Encoding.UTF8.GetBytes("not complete"), halfClose: false));
    }
    [Fact]
    public async Task TcpRateLimitRejectsTwentyFirstConnection()
    {
        await using var server = new Server(); await server.StartAsync();
        for (var i = 0; i < 20; i++) Assert.StartsWith("https://", await server.SendAsync(Encoding.UTF8.GetBytes("test")));
        Assert.StartsWith("ERROR 429:", await server.SendAsync([]));
    }
}
