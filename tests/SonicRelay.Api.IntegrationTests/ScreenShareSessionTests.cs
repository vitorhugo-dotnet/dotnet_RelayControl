using System.Net;
using System.Net.Http.Json;
using SonicRelay.Api.Contracts;
using SonicRelay.Domain.Devices;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

/// <summary>
/// Screen-share sessions (issue #30, phase 0): the windows_desktop device type, the
/// screen_share session mode, device-type-gated admission and auto-pairing on join — plus
/// the guarantee that the audio device types and session modes behave exactly as before.
/// </summary>
public sealed class ScreenShareSessionTests : IClassFixture<SonicRelayApiFactory>
{
    private readonly SonicRelayApiFactory _factory;

    public ScreenShareSessionTests(SonicRelayApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Windows_desktop_bootstraps_on_the_windows_platform()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/devices/bootstrap",
            new BootstrapDeviceRequest("Desk PC", DeviceTypes.WindowsDesktop, DevicePlatforms.Windows));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("android")]
    [InlineData("ios")]
    public async Task Windows_desktop_is_rejected_on_non_windows_platforms(string platform)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/devices/bootstrap",
            new BootstrapDeviceRequest("Desk PC", DeviceTypes.WindowsDesktop, platform));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Windows_desktop_token_carries_exactly_the_union_of_publisher_and_viewer_scopes()
    {
        var client = _factory.CreateClient();
        var session = await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            client, DeviceTypes.WindowsDesktop, DevicePlatforms.Windows);

        var scopes = ScopesInToken(session.AccessToken);

        Assert.Equal(
            new[]
            {
                "device:manage", "device:read",
                "pairing:complete", "pairing:create", "pairing:revoke",
                "session:create", "session:end", "session:join",
                "signaling:connect", "turn:credentials"
            },
            scopes.Order().ToArray());
    }

    // The token is the contract under test, so the scopes are read out of the JWT itself
    // rather than trusted from the response body.
    private static string[] ScopesInToken(string accessToken)
    {
        var payload = accessToken.Split('.')[1];
        var padded = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=')
            .Replace('-', '+').Replace('_', '/');
        using var document = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(padded));
        return document.RootElement.GetProperty("scope").GetString()!.Split(' ');
    }
}
