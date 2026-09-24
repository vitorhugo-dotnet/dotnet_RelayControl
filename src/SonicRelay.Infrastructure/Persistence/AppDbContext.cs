using Microsoft.EntityFrameworkCore;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Domain.LaunchIntents;
using SonicRelay.Domain.RelaySettings;
using SonicRelay.Domain.Sessions;
using SonicRelay.Domain.Signaling;

namespace SonicRelay.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<StreamSession> StreamSessions => Set<StreamSession>();
    public DbSet<SessionParticipant> SessionParticipants => Set<SessionParticipant>();
    public DbSet<SignalingEvent> SignalingEvents => Set<SignalingEvent>();
    public DbSet<DeviceIdentity> DeviceIdentities => Set<DeviceIdentity>();
    public DbSet<PairingChallenge> PairingChallenges => Set<PairingChallenge>();
    public DbSet<DevicePairing> DevicePairings => Set<DevicePairing>();
    public DbSet<RelayDeviceSettings> RelayDeviceSettings => Set<RelayDeviceSettings>();
    public DbSet<ShareLaunchIntent> ShareLaunchIntents => Set<ShareLaunchIntent>();
    public DbSet<WatchLaunchCapability> WatchLaunchCapabilities => Set<WatchLaunchCapability>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<StreamSession>(entity =>
        {
            entity.ToTable("stream_sessions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            // Sessions created before duplex existed are one-way, and so is anything a client
            // creates without asking for a mode; the column default keeps both cases honest
            // without a backfill that has to guess.
            entity.Property(x => x.Mode).HasMaxLength(16).IsRequired().HasDefaultValue(SessionModes.Broadcast);
            entity.HasIndex(x => new { x.SourceDeviceId, x.Status }).HasDatabaseName("ix_stream_sessions_source_device_status");
            // The data-retention sweep (issue #44) scans by collection time, not by status.
            entity.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_stream_sessions_created_at");
        });

        modelBuilder.Entity<SessionParticipant>(entity =>
        {
            entity.ToTable("session_participants");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Role).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            // Audio capabilities default to the broadcast shape (publishers transmit, viewers
            // listen); the migration backfills existing rows by role for the same reason.
            entity.Property(x => x.AudioSendAllowed).HasDefaultValue(false);
            entity.Property(x => x.CanSendAudio).HasDefaultValue(false);
            entity.Property(x => x.CanReceiveAudio).HasDefaultValue(true);
            entity.Property(x => x.AudioMuted).HasDefaultValue(false);
            entity.HasIndex(x => new { x.SessionId, x.Role }).HasDatabaseName("ix_session_participants_session_role");
            // A device holds at most one participant row per role in a session. Rejoin after a
            // network loss is a read-then-insert, and without this two concurrent attempts from
            // the same device could each insert a row — consuming a viewer slot twice and
            // splitting signaling routing across two participant ids.
            entity.HasIndex(x => new { x.SessionId, x.DeviceId, x.Role })
                .IsUnique()
                .HasDatabaseName("ux_session_participants_session_device_role");
            entity.HasIndex(x => x.JoinedAt).HasDatabaseName("ix_session_participants_joined_at");
        });

        modelBuilder.Entity<SignalingEvent>(entity =>
        {
            entity.ToTable("signaling_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EventType).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => new { x.SessionId, x.CreatedAt }).HasDatabaseName("ix_signaling_events_session_created_at");
            entity.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_signaling_events_created_at");
        });

        modelBuilder.Entity<DeviceIdentity>(entity =>
        {
            entity.ToTable("device_identities");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.DeviceType).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Platform).HasMaxLength(40).IsRequired();
            entity.Property(x => x.CredentialSecretHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.HasIndex(x => x.Status).HasDatabaseName("ix_device_identities_status");
            // Both the retention sweep and the identity-rotation deadline are measured from
            // CreatedAt, which is the moment the identifier was collected.
            entity.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_device_identities_created_at");
        });

        modelBuilder.Entity<PairingChallenge>(entity =>
        {
            entity.ToTable("pairing_challenges");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CodeHash).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => x.PublisherDeviceId).HasDatabaseName("ix_pairing_challenges_publisher_device_id");
            entity.HasIndex(x => x.ExpiresAt).HasDatabaseName("ix_pairing_challenges_expires_at");
            entity.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_pairing_challenges_created_at");
        });

        modelBuilder.Entity<DevicePairing>(entity =>
        {
            entity.ToTable("device_pairings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.HasIndex(x => x.PublisherDeviceId).HasDatabaseName("ix_device_pairings_publisher_device_id");
            entity.HasIndex(x => x.ViewerDeviceId).HasDatabaseName("ix_device_pairings_viewer_device_id");
            entity.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_device_pairings_created_at");
        });

        modelBuilder.Entity<RelayDeviceSettings>(entity =>
        {
            entity.ToTable("relay_device_settings");
            entity.HasKey(x => x.DeviceId);
            entity.Property(x => x.RelayMode).HasMaxLength(20).IsRequired();
            entity.Property(x => x.TurnUsername).HasMaxLength(256);
            entity.Property(x => x.TurnCredential).HasMaxLength(256);
            entity.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_relay_device_settings_created_at");
        });

        modelBuilder.Entity<ShareLaunchIntent>(entity =>
        {
            entity.ToTable("share_launch_intents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("ux_share_launch_intents_token_hash");
            entity.Property(x => x.Provider).HasMaxLength(32).IsRequired();
            entity.Property(x => x.GuildId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ChannelId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.RequestedByUserId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(24).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.Status, x.ExpiresAt }).HasDatabaseName("ix_share_launch_intents_status_expires_at");
            entity.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_share_launch_intents_created_at");
        });

        modelBuilder.Entity<WatchLaunchCapability>(entity =>
        {
            entity.ToTable("watch_launch_capabilities");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("ux_watch_launch_capabilities_token_hash");
            entity.HasIndex(x => new { x.SessionId, x.ExpiresAt }).HasDatabaseName("ix_watch_launch_capabilities_session_expires_at");
        });
    }
}
