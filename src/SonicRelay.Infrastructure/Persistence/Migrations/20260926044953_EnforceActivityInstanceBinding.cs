using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonicRelay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceActivityInstanceBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_launch_capabilities_InstanceId",
                table: "launch_capabilities",
                column: "InstanceId",
                unique: true,
                filter: "\"Kind\" = 'activity' AND \"InstanceId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_launch_capabilities_InstanceId",
                table: "launch_capabilities");
        }
    }
}
