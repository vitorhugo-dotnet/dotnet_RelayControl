using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SonicRelay.MediaRelay;

// Local codec fixture only. Production uses RelayControl's authenticated grant endpoints.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5174");
builder.Configuration["MediaRelay:ServiceToken"] = "fixture";
builder.Services.AddSingleton<MediaRelayRegistry>();
builder.Services.AddSingleton(new RelayControlLeaseClient(new HttpClient(new FixtureApi()) { BaseAddress = new("http://fixture/") }, builder.Configuration));
var app = builder.Build(); app.UseWebSockets(); app.MapGet("/ws/media", MediaSocketEndpoint.HandleAsync); app.Run();

sealed class FixtureApi : HttpMessageHandler
{
    private readonly Guid session = Guid.NewGuid();
    private readonly Dictionary<Guid, MediaLease> leases = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        lock (leases)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("redeem"))
            {
                var grant = json.RootElement.GetProperty("grant").GetString();
                if (grant is not { Length: 64 } || grant[0] is not ('u' or 'v')) return new(HttpStatusCode.Unauthorized);
                var lease = new MediaLease(Guid.NewGuid(), session, null, grant[0] == 'u' ? "upload" : "view", DateTimeOffset.UtcNow.AddSeconds(30));
                leases[lease.LeaseId] = lease; return new(HttpStatusCode.OK) { Content = JsonContent.Create(lease) };
            }
            var id = json.RootElement.GetProperty("leaseId").GetGuid();
            if (request.RequestUri.AbsolutePath.EndsWith("release")) { leases.Remove(id); return new(HttpStatusCode.NoContent); }
            return leases.TryGetValue(id, out var current) ? new(HttpStatusCode.OK) { Content = JsonContent.Create(current with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30) }) } : new(HttpStatusCode.Unauthorized);
        }
    }
}
