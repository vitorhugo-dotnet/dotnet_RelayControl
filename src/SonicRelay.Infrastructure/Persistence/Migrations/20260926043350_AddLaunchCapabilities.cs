using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonicRelay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLaunchCapabilities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "launch_capabilities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    GuildId = table.Column<string>(type: "text", nullable: false),
                    ChannelId = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    InstanceId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ParticipantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Code = table.Column<string>(type: "text", nullable: true),
                    MessageId = table.Column<string>(type: "text", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_launch_capabilities", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_launch_capabilities_ExpiresAt",
                table: "launch_capabilities",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_launch_capabilities_TokenHash",
                table: "launch_capabilities",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "launch_capabilities");
        }
    }
}
