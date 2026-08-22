using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonicRelay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDuplexAudioCapabilities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "stream_sessions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "broadcast");

            migrationBuilder.AddColumn<bool>(
                name: "AudioMuted",
                table: "session_participants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AudioSendAllowed",
                table: "session_participants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanReceiveAudio",
                table: "session_participants",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "CanSendAudio",
                table: "session_participants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Every pre-existing session is a broadcast (the Mode column defaults to it), so the
            // column defaults are already correct for its viewers. Its publishers are not: they
            // are transmitting right now, and leaving AudioSendAllowed=false would make the
            // first participant.capabilities announcement after the deployment fail with
            // audio_send_not_authorized on a stream that is working. This is exactly
            // SessionAudioPolicy.DefaultsFor(broadcast, publisher), applied to the rows that
            // predate it.
            migrationBuilder.Sql("""
                UPDATE session_participants
                SET "AudioSendAllowed" = TRUE,
                    "CanSendAudio" = TRUE,
                    "CanReceiveAudio" = FALSE
                WHERE "Role" = 'publisher';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Mode",
                table: "stream_sessions");

            migrationBuilder.DropColumn(
                name: "AudioMuted",
                table: "session_participants");

            migrationBuilder.DropColumn(
                name: "AudioSendAllowed",
                table: "session_participants");

            migrationBuilder.DropColumn(
                name: "CanReceiveAudio",
                table: "session_participants");

            migrationBuilder.DropColumn(
                name: "CanSendAudio",
                table: "session_participants");
        }
    }
}
