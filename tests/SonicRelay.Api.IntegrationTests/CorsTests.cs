using System.Net;
using System.Net.Http.Json;
using SonicRelay.Api.Contracts;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

/// <summary>
/// The Flutter viewer also ships as a web build served from
/// <c>https://sonicrelay.hugodotnet.dev</c>, so every call it makes to this API is
/// cross-origin. Without a CORS policy the browser's preflight
/// <c>OPTIONS /api/devices/bootstrap</c> hit routing's method fallback (405, no
/// <c>Access-Control-Allow-Origin</c>) and the viewer never got past device setup.
/// These tests pin the allowlist that unblocks it.
/// </summary>
public sealed class CorsTests
{
    private const string WebViewerOrigin = "https://sonicrelay.hugodotnet.dev";
    private const string BootstrapPath = "/api/devices/bootstrap";

    private static HttpRequestMessage Preflight(string origin, string method = "POST") =>
        new(HttpMethod.Options, BootstrapPath)
        {
            Headers =
            {
                { "Origin", origin },
                { "Access-Control-Request-Method", method },
                { "Access-Control-Request-Headers", "content-type" }
            }
        };

    [Fact]
    public async Task Preflight_from_the_web_viewer_is_answered_instead_of_405()
    {
        using var factory = new SonicRelayApiFactory();

        var response = await factory.CreateClient().SendAsync(Preflight(WebViewerOrigin));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(WebViewerOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains("POST", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Methods")));
    }

    [Fact]
    public async Task Bootstrap_response_is_readable_by_the_web_viewer()
    {
        using var factory = new SonicRelayApiFactory();
        using var request = new HttpRequestMessage(HttpMethod.Post, BootstrapPath)
        {
            Headers = { { "Origin", WebViewerOrigin } },
            Content = JsonContent.Create(new BootstrapDeviceRequest("Chrome on Windows", "flutter_viewer", "web"))
        };

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(WebViewerOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task A_rejected_bootstrap_is_still_readable_so_the_viewer_can_show_the_error()
    {
        using var factory = new SonicRelayApiFactory();
        using var request = new HttpRequestMessage(HttpMethod.Post, BootstrapPath)
        {
            Headers = { { "Origin", WebViewerOrigin } },
            Content = JsonContent.Create(new BootstrapDeviceRequest("", "", ""))
        };

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(WebViewerOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task An_origin_outside_the_allowlist_gets_no_allow_origin_header()
    {
        using var factory = new SonicRelayApiFactory();

        var response = await factory.CreateClient().SendAsync(Preflight("https://not-sonicrelay.example"));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task A_configured_allowlist_replaces_the_built_in_default()
    {
        using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins:0"] = "https://viewer.example",
            ["Cors:AllowLoopbackOrigins"] = "false"
        });
        var client = factory.CreateClient();

        var allowed = await client.SendAsync(Preflight("https://viewer.example"));
        var previousDefault = await client.SendAsync(Preflight(WebViewerOrigin));

        Assert.Equal("https://viewer.example", Assert.Single(allowed.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(previousDefault.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task A_trailing_slash_in_configuration_still_matches_the_browser_origin()
    {
        using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins:0"] = "https://viewer.example/"
        });

        var response = await factory.CreateClient().SendAsync(Preflight("https://viewer.example"));

        Assert.Equal("https://viewer.example", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task A_blank_configured_origin_falls_back_to_the_default_instead_of_allowing_none()
    {
        // Compose forwards an unset CORS_ALLOWED_ORIGIN as an empty string; taking that
        // literally would leave the published viewer with no allowed origin at all.
        using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins:0"] = ""
        });

        var response = await factory.CreateClient().SendAsync(Preflight(WebViewerOrigin));

        Assert.Equal(WebViewerOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Theory]
    [InlineData("http://localhost:57193")]
    [InlineData("http://127.0.0.1:8000")]
    public async Task Loopback_origins_are_allowed_outside_production_for_flutter_run(string origin)
    {
        using var factory = new SonicRelayApiFactory();

        var response = await factory.CreateClient().SendAsync(Preflight(origin));

        Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task Loopback_origins_are_refused_in_production_by_default()
    {
        using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["environment"] = "Production"
        });

        var response = await factory.CreateClient().SendAsync(Preflight("http://localhost:57193"));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Preflight_never_spends_the_bootstrap_rate_limit_budget()
    {
        using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["RateLimits:DeviceBootstrap:PermitLimit"] = "1"
        });
        var client = factory.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            var preflight = await client.SendAsync(Preflight(WebViewerOrigin));
            Assert.Equal(HttpStatusCode.NoContent, preflight.StatusCode);
        }
    }
}
