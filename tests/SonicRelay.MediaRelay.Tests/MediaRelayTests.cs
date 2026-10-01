using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SonicRelay.MediaProtocol;
using SonicRelay.MediaRelay;

namespace SonicRelay.MediaRelay.Tests;

public sealed class MediaRelayTests
{
    [Fact]
    public async Task Queue_bounds_messages_and_bytes_and_clear_does_not_terminate_reader()
    {
        var queue = new MediaConnectionQueue(); var sample = new MediaMessage(2, 1, 1, 0, 0, 0, new byte[300000]);
        var count = 0; while (queue.TryEnqueue(sample)) count++;
        Assert.InRange(count, 1, 63);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        queue.Clear(); var pending = queue.ReadAsync(ct.Token).AsTask(); queue.TryEnqueue(sample);
        Assert.Equal(sample.Payload.Length, (await pending).Payload.Length);
        queue.Clear(); sample = sample with { Payload = ReadOnlyMemory<byte>.Empty };
        for (var i = 0; i < 64; i++) Assert.True(queue.TryEnqueue(sample));
        Assert.False(queue.TryEnqueue(sample));
    }

    private sealed class Api(Guid session) : HttpMessageHandler
    {
        public double LifetimeSeconds = 30;
        public readonly List<Guid> Released = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (request.RequestUri!.AbsolutePath.EndsWith("/release"))
            { lock (Released) Released.Add(json.RootElement.GetProperty("connectionId").GetGuid()); return new(HttpStatusCode.NoContent); }
            var role = "view";
            if (request.RequestUri.AbsolutePath.EndsWith("/redeem"))
            {
                var grant = json.RootElement.GetProperty("grant").GetString()!;
                if (grant[0] == 'x') return new(HttpStatusCode.Unauthorized);
                role = grant[0] == 'u' ? "upload" : "view";
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new MediaLease(Guid.NewGuid(), session, null, role, DateTimeOffset.UtcNow.AddSeconds(LifetimeSeconds))) };
        }
    }
    private sealed class Host : IAsyncDisposable
    {
        public readonly CancellationTokenSource Budget = new(TimeSpan.FromSeconds(10));
        public readonly Api Api = new(Guid.NewGuid());
        private readonly WebApplication app;
        public Host()
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["MediaRelay:ServiceToken"] = "test" });
            builder.Services.AddSingleton<MediaRelayRegistry>();
            builder.Services.AddSingleton(new RelayControlLeaseClient(new HttpClient(Api) { BaseAddress = new("http://api/") }, builder.Configuration));
            app = builder.Build(); app.UseWebSockets(); app.MapGet("/ws/media", MediaSocketEndpoint.HandleAsync);
        }
        public async Task<ClientWebSocket> Connect(string grant)
        {
            if (app.Urls.All(x => x.EndsWith(":0"))) await app.StartAsync(Budget.Token);
            var socket = new ClientWebSocket(); socket.Options.AddSubProtocol("framerelay-media-v1");
            await socket.ConnectAsync(new Uri(app.Urls.Single().Replace("http:", "ws:") + "/ws/media"), Budget.Token);
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { grant = new string(grant[0], 64) }).AsMemory(), WebSocketMessageType.Text, true, Budget.Token);
            return socket;
        }
        public async ValueTask DisposeAsync() { Budget.Cancel(); await app.StopAsync(); await app.DisposeAsync(); Budget.Dispose(); }
    }

    [Fact]
    public async Task Viewers_receive_identical_samples_and_duplicate_upload_is_rejected()
    {
        await using var host = new Host(); using var upload = await host.Connect("u");
        var config = new MediaMessage(1, 0, 1, 0, 0, 0, JsonSerializer.SerializeToUtf8Bytes(new { video = new { codec = "avc1.42e01f", width = 640, height = 360, format = "annexb" }, audio = (object?)null }));
        await Send(upload, config, host.Budget.Token);
        Assert.Equal(4, (await Receive(upload, host.Budget.Token)).Type);
        using var first = await host.Connect("v"); using var second = await host.Connect("v");
        Assert.Equal(1, (await Receive(first, host.Budget.Token)).Type); Assert.Equal(1, (await Receive(second, host.Budget.Token)).Type);
        var key = new MediaMessage(2, 1, 1, 1, 1000000, 16667, new byte[] { 0, 0, 0, 1, 0x65 });
        await Send(upload, key, host.Budget.Token);
        Assert.Equal(MediaWireCodec.Encode(key), MediaWireCodec.Encode(await Receive(first, host.Budget.Token)));
        Assert.Equal(MediaWireCodec.Encode(key), MediaWireCodec.Encode(await Receive(second, host.Budget.Token)));
        using var duplicate = await host.Connect("u");
        await Assert.ThrowsAnyAsync<WebSocketException>(() => MediaSocketEndpoint.ReceiveAsync(duplicate, WebSocketMessageType.Binary, 1024, host.Budget.Token));
        var delta = key with { Flags = 0, Sequence = 2 }; await Send(upload, delta, host.Budget.Token);
        Assert.Equal(2u, (await Receive(first, host.Budget.Token)).Sequence);
    }
    [Fact]
    public async Task Unknown_grant_cannot_subscribe()
    {
        await using var host = new Host(); using var socket = await host.Connect("x");
        await Assert.ThrowsAnyAsync<WebSocketException>(() => MediaSocketEndpoint.ReceiveAsync(socket, WebSocketMessageType.Binary, 1024, host.Budget.Token));
    }
    [Fact]
    public async Task Lease_expiry_closes_socket()
    {
        await using var host = new Host(); host.Api.LifetimeSeconds = 0.5;
        using var socket = await host.Connect("v");
        await Assert.ThrowsAnyAsync<WebSocketException>(() => MediaSocketEndpoint.ReceiveAsync(socket, WebSocketMessageType.Binary, 1024, host.Budget.Token));
    }
    [Fact]
    public async Task Sequence_gap_discards_delta_until_fresh_keyframe_and_new_generation_reconfigures()
    {
        await using var host = new Host(); using var upload = await host.Connect("u");
        var config = new MediaMessage(1, 0, 1, 0, 0, 0, JsonSerializer.SerializeToUtf8Bytes(new { video = new { codec = "avc1.42e01f", width = 640, height = 360, format = "annexb" }, audio = (object?)null }));
        await Send(upload, config, host.Budget.Token); await Receive(upload, host.Budget.Token);
        using var viewer = await host.Connect("v"); await Receive(viewer, host.Budget.Token);
        var key = new MediaMessage(2, 1, 1, 1, 0, 16667, new byte[] { 0, 0, 0, 1, 0x65 });
        await Send(upload, key, host.Budget.Token); await Receive(viewer, host.Budget.Token);
        await Send(upload, key with { Sequence = 3, Flags = 0 }, host.Budget.Token);
        await Send(upload, key with { Sequence = 4 }, host.Budget.Token);
        Assert.Equal(4u, (await Receive(viewer, host.Budget.Token)).Sequence);
        await Send(upload, config with { Generation = 2 }, host.Budget.Token);
        Assert.Equal(2u, (await Receive(viewer, host.Budget.Token)).Generation);
        await Send(upload, key with { Generation = 2 }, host.Budget.Token);
        Assert.Equal(2u, (await Receive(viewer, host.Budget.Token)).Generation);
    }
    [Fact]
    public async Task Persistently_slow_viewer_is_closed_while_other_viewer_receives()
    {
        await using var host = new Host(); using var upload = await host.Connect("u");
        var config = new MediaMessage(1, 0, 1, 0, 0, 0, JsonSerializer.SerializeToUtf8Bytes(new { video = new { codec = "avc1.42e01f", width = 640, height = 360, format = "annexb" }, audio = (object?)null }));
        await Send(upload, config, host.Budget.Token); await Receive(upload, host.Budget.Token);
        using var slow = await host.Connect("v"); await Receive(slow, host.Budget.Token);
        using var fast = await host.Connect("v"); await Receive(fast, host.Budget.Token);
        var received = System.Threading.Channels.Channel.CreateUnbounded<uint>();
        var fastRead = Task.Run(async () => {
            uint sequence;
            do { sequence = (await Receive(fast, host.Budget.Token)).Sequence; received.Writer.TryWrite(sequence); } while (sequence < 160);
        });
        var payload = new byte[700000];
        for (uint i = 1; i <= 160; i++)
        {
            await Send(upload, new MediaMessage(2, 1, 1, i, i * 16667, 16667, payload), host.Budget.Token);
            if (i % 16 == 0) while (await received.Reader.ReadAsync(host.Budget.Token) < i) { }
        }
        await fastRead;
        using var closeBudget = CancellationTokenSource.CreateLinkedTokenSource(host.Budget.Token); closeBudget.CancelAfter(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<WebSocketException>(async () => {
            while (await MediaSocketEndpoint.ReceiveAsync(slow, WebSocketMessageType.Binary, MediaWireCodec.MaxMessageSize, closeBudget.Token) is not null) { }
        });
    }
    private static Task Send(ClientWebSocket socket, MediaMessage sample, CancellationToken ct) => socket.SendAsync(MediaWireCodec.Encode(sample).AsMemory(), WebSocketMessageType.Binary, true, ct).AsTask();
    private static async Task<MediaMessage> Receive(ClientWebSocket socket, CancellationToken ct) => MediaWireCodec.Decode((await MediaSocketEndpoint.ReceiveAsync(socket, WebSocketMessageType.Binary, MediaWireCodec.MaxMessageSize, ct))!);
}
