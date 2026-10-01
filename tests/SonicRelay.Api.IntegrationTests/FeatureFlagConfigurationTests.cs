using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FeatureManagement;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class FeatureFlagConfigurationTests
{
    [Theory]
    [InlineData("PublicRooms")]
    [InlineData("DiscordActivity")]
    [InlineData("DuplexAudio")]
    [InlineData("ScreenShare")]
    public async Task Missing_flags_are_enabled(string feature)
    {
        await using var factory = new SonicRelayApiFactory();
        Assert.True(await factory.Services.GetRequiredService<IVariantFeatureManager>().IsEnabledAsync(feature));
    }

    [Theory]
    [InlineData("PublicRooms")]
    [InlineData("DiscordActivity")]
    [InlineData("DuplexAudio")]
    [InlineData("ScreenShare")]
    public async Task Explicit_false_overrides_defaults(string feature)
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            [$"FeatureManagement:{feature}"] = "false"
        });
        Assert.False(await factory.Services.GetRequiredService<IVariantFeatureManager>().IsEnabledAsync(feature));
    }

    [Fact]
    public async Task Authentication_is_not_disabled_by_feature_flags()
    {
        await using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["FeatureManagement:ScreenShare"] = "false"
        });
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/sessions/active")).StatusCode);
    }
}
