using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace SonicRelay.MediaRelay;

public sealed record MediaLease(Guid LeaseId, Guid SessionId, Guid? ParticipantId, string Role, DateTimeOffset ExpiresAt);
public sealed class RelayControlLeaseClient(HttpClient http, IConfiguration configuration)
{
    private async Task<MediaLease?> SendAsync(string path, object body, CancellationToken ct)
    {
        var secret = configuration["MediaRelay:ServiceToken"];
        if (string.IsNullOrWhiteSpace(secret)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        using var response = await http.SendAsync(request, ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<MediaLease>(ct) : null;
    }
    public Task<MediaLease?> RedeemAsync(string grant, Guid connectionId, CancellationToken ct) => SendAsync("api/internal/media-relay/redeem", new { grant, connectionId }, ct);
    public Task<MediaLease?> RenewAsync(Guid leaseId, Guid connectionId, CancellationToken ct) => SendAsync("api/internal/media-relay/renew", new { leaseId, connectionId }, ct);
    public async Task ReleaseAsync(Guid leaseId, Guid connectionId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/internal/media-relay/release") { Content = JsonContent.Create(new { leaseId, connectionId }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration["MediaRelay:ServiceToken"]);
        using var response = await http.SendAsync(request, ct);
    }
}
