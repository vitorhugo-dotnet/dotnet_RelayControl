using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SonicRelay.Api.Services;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class SignalingGrantServiceTests
{
    private const string TokenSigningKey = "unit-test-signaling-token-signing-key-needs-32-bytes-min";

    [Fact]
    public void Issue_BindsGrantToDeviceSessionAndParticipant_ForSixtySeconds()
    {
        var now = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var clock = new TestTimeProvider(now);
        var service = CreateService(clock);
        var deviceId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();

        var issued = service.Issue(deviceId, sessionId, participantId);
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(issued.Token, service.ValidationParameters, out _);

        Assert.Equal(deviceId.ToString(), principal.FindFirst("sub")!.Value);
        Assert.Equal(sessionId.ToString(), principal.FindFirst("session_id")!.Value);
        Assert.Equal(participantId.ToString(), principal.FindFirst("participant_id")!.Value);
        Assert.Equal("signaling", principal.FindFirst("purpose")!.Value);
        Assert.True(Guid.TryParse(principal.FindFirst("jti")!.Value, out _));
        Assert.Equal(now.AddSeconds(60), issued.ExpiresAt);
    }

    [Fact]
    public void Issue_NormalizesFractionalSecondsToTheJwtExpiryBoundary()
    {
        var now = new DateTimeOffset(2030, 1, 2, 3, 4, 5, 900, TimeSpan.Zero);
        var clock = new TestTimeProvider(now);
        var service = CreateService(clock);

        var issued = service.Issue(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(new DateTimeOffset(2030, 1, 2, 3, 5, 5, TimeSpan.Zero), issued.ExpiresAt);
        clock.Advance(TimeSpan.FromSeconds(59));
        new JwtSecurityTokenHandler { MapInboundClaims = false }
            .ValidateToken(issued.Token, service.ValidationParameters, out _);
        clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.ThrowsAny<SecurityTokenException>(() =>
            new JwtSecurityTokenHandler { MapInboundClaims = false }
                .ValidateToken(issued.Token, service.ValidationParameters, out _));
    }

    [Fact]
    public void ValidationParameters_RejectTamperedGrant()
    {
        var service = CreateService(new TestTimeProvider(new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero)));
        var issued = service.Issue(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var tokenParts = issued.Token.Split('.');
        tokenParts[2] = $"{(tokenParts[2][0] == 'A' ? 'B' : 'A')}{tokenParts[2][1..]}";

        Assert.ThrowsAny<SecurityTokenException>(() =>
            new JwtSecurityTokenHandler { MapInboundClaims = false }
                .ValidateToken(string.Join('.', tokenParts), service.ValidationParameters, out _));
    }

    [Fact]
    public void ValidationParameters_RejectGrantForWrongAudience()
    {
        var service = CreateService(new TestTimeProvider(new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero)));
        var issued = service.Issue(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var validationParameters = service.ValidationParameters.Clone();
        validationParameters.ValidAudience = "another-service:signaling";

        Assert.Throws<SecurityTokenInvalidAudienceException>(() =>
            new JwtSecurityTokenHandler { MapInboundClaims = false }
                .ValidateToken(issued.Token, validationParameters, out _));
    }

    [Fact]
    public void ValidationParameters_RejectExpiredGrantUsingInjectedClock()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var service = CreateService(clock);
        var issued = service.Issue(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        clock.Advance(TimeSpan.FromSeconds(61));

        Assert.ThrowsAny<SecurityTokenException>(() =>
            new JwtSecurityTokenHandler { MapInboundClaims = false }
                .ValidateToken(issued.Token, service.ValidationParameters, out _));
    }

    private static SignalingGrantService CreateService(TimeProvider time) => new(
        Options.Create(new DeviceIdentityOptions
        {
            TokenSigningKey = TokenSigningKey,
            Issuer = "sonicrelay-tests",
            Audience = "sonicrelay-devices-tests"
        }),
        time);
}
