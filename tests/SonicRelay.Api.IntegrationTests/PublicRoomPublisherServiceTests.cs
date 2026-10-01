using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonicRelay.Api.Services;
using SonicRelay.Domain.Sessions;
using SonicRelay.Infrastructure.Persistence;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class PublicRoomPublisherServiceTests
{
    [Fact]
    public async Task ExecuteAsync_does_nothing_when_disabled()
    {
        var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["PublicRoom:Enabled"] = "false"
        });
        _ = factory.CreateClient(); // forces host startup, including hosted services

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SonicRelay.Infrastructure.Persistence.AppDbContext>();
        // A disabled publisher must never seed the session, matching Task 5's endpoint test.
        Assert.False(await db.StreamSessions.AnyAsync(x => x.Id == PublicRoomSeeder.PublicSessionId));
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task ExecuteAsync_seeds_the_session_when_enabled_even_with_no_tracks()
    {
        var emptyTracksDir = Path.Combine(Path.GetTempPath(), "sonicrelay-public-room-empty-" + Guid.NewGuid());
        Directory.CreateDirectory(emptyTracksDir);
        var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["PublicRoom:Enabled"] = "true",
            ["PublicRoom:TracksPath"] = emptyTracksDir,
        });
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SonicRelay.Infrastructure.Persistence.AppDbContext>();
        // Wait for the actual startup milestone rather than assuming a loaded runner finishes in one second.
        using var startupBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await db.StreamSessions.AnyAsync(x => x.Id == PublicRoomSeeder.PublicSessionId, startupBudget.Token))
            await Task.Delay(20, startupBudget.Token);
        Assert.True(await db.StreamSessions.AnyAsync(x => x.Id == PublicRoomSeeder.PublicSessionId));
        await factory.DisposeAsync();
        Directory.Delete(emptyTracksDir, recursive: true);
    }

    /// <summary>
    /// The handshake's first half (docs/protocol.md): a viewer cannot address anything to the
    /// publisher until the publisher sends it a publisher.ready, because the publisher's
    /// participant id is only ever learned from that frame's authenticated `from`. Before this was
    /// handled, the whole feature deadlocked here — the publisher only ever reacted to
    /// viewer.ready, which no viewer could send.
    /// </summary>
    [Fact]
    public async Task Sends_publisher_ready_when_the_server_announces_a_viewer()
    {
        await using var factory = DisabledFactory();
        var service = factory.Services.GetRequiredService<PublicRoomPublisherService>();
        using var server = new FakeSignalingServer();
        await using var client = await ConnectAsync(server);

        var viewerParticipantId = Guid.NewGuid();
        await service.HandleSignalingMessageAsync(client, viewerParticipantId, "session.joined",
            Announcement(viewerParticipantId, ParticipantRoles.Viewer), CancellationToken.None);

        var message = Assert.Single(await server.WaitForMessagesAsync(1, TimeSpan.FromSeconds(5)));
        Assert.Equal("publisher.ready", message.GetProperty("type").GetString());
        Assert.Equal(viewerParticipantId, message.GetProperty("to").GetGuid());
    }

    /// <summary>
    /// A viewer that drops and comes back inside the disconnect grace period is announced as
    /// participant.reconnected rather than session.joined, and needs the same publisher.ready to
    /// renegotiate. The publisher's own self-announcement (role "publisher") must not produce one.
    /// </summary>
    [Fact]
    public async Task Answers_reconnected_viewers_but_ignores_publisher_announcements()
    {
        await using var factory = DisabledFactory();
        var service = factory.Services.GetRequiredService<PublicRoomPublisherService>();
        using var server = new FakeSignalingServer();
        await using var client = await ConnectAsync(server);

        var otherPublisherId = Guid.NewGuid();
        await service.HandleSignalingMessageAsync(client, otherPublisherId, "session.joined",
            Announcement(otherPublisherId, ParticipantRoles.Publisher), CancellationToken.None);

        var viewerParticipantId = Guid.NewGuid();
        await service.HandleSignalingMessageAsync(client, viewerParticipantId, "participant.reconnected",
            Announcement(viewerParticipantId, ParticipantRoles.Viewer), CancellationToken.None);

        var messages = await server.WaitForMessagesAsync(1, TimeSpan.FromSeconds(5));
        await Task.Delay(200); // let any (unwanted) extra frame land before asserting the count
        messages = server.Received;
        var message = Assert.Single(messages);
        Assert.Equal("publisher.ready", message.GetProperty("type").GetString());
        Assert.Equal(viewerParticipantId, message.GetProperty("to").GetGuid());
    }

    /// <summary>
    /// The signaling server only announces new arrivals, so a publisher that reconnects (its
    /// socket dropped, or it started after viewers were already in the room) hears nothing about
    /// the viewers already there. Those viewers must be re-announced from the DB, or they stay
    /// stranded with no offer for the rest of the session.
    /// </summary>
    [Fact]
    public async Task Announces_publisher_ready_to_viewers_that_were_already_connected()
    {
        await using var factory = DisabledFactory();
        var service = factory.Services.GetRequiredService<PublicRoomPublisherService>();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var connectedViewerId = AddParticipant(db, ParticipantRoles.Viewer, ParticipantStatuses.Connected);
        var reconnectingViewerId = AddParticipant(db, ParticipantRoles.Viewer, ParticipantStatuses.Reconnecting);
        AddParticipant(db, ParticipantRoles.Viewer, ParticipantStatuses.Disconnected);
        AddParticipant(db, ParticipantRoles.Publisher, ParticipantStatuses.Connected);
        await db.SaveChangesAsync();

        using var server = new FakeSignalingServer();
        await using var client = await ConnectAsync(server);

        await service.AnnounceReadyToExistingViewersAsync(client, db, CancellationToken.None);

        await server.WaitForMessagesAsync(2, TimeSpan.FromSeconds(5));
        await Task.Delay(200); // a viewer that should have been skipped would arrive here
        var messages = server.Received;
        Assert.All(messages, m => Assert.Equal("publisher.ready", m.GetProperty("type").GetString()));
        Assert.Equal(
            new HashSet<Guid> { connectedViewerId, reconnectingViewerId },
            messages.Select(m => m.GetProperty("to").GetGuid()).ToHashSet());
    }

    private static SonicRelayApiFactory DisabledFactory()
    {
        // Enabled=false keeps the hosted service a no-op so these tests drive the handshake
        // handlers directly against a fake signaling server, with no background publisher racing
        // them for the same fake socket or the same DB rows.
        var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["PublicRoom:Enabled"] = "false"
        });
        _ = factory.CreateClient();
        return factory;
    }

    private static async Task<PublicRoomSignalingClient> ConnectAsync(FakeSignalingServer server)
    {
        var client = new PublicRoomSignalingClient(new Uri(server.HttpBaseUrl), "test-token");
        var connectTask = client.ConnectAsPublisherAsync(PublicRoomSeeder.PublicSessionId, CancellationToken.None);
        await server.WaitForConnectionAsync();
        await server.SendSelfJoinedAsync(Guid.NewGuid());
        await connectTask;
        return client;
    }

    private static Guid AddParticipant(AppDbContext db, string role, string status)
    {
        var participant = new SessionParticipant
        {
            Id = Guid.NewGuid(),
            SessionId = PublicRoomSeeder.PublicSessionId,
            DeviceId = Guid.NewGuid(),
            Role = role,
            Status = status,
            JoinedAt = DateTimeOffset.UtcNow
        };
        db.SessionParticipants.Add(participant);
        return participant.Id;
    }

    private static JsonElement Announcement(Guid participantId, string role)
    {
        using var document = JsonDocument.Parse(
            $$"""{"participantId":"{{participantId}}","role":"{{role}}"}""");
        return document.RootElement.Clone();
    }
}
