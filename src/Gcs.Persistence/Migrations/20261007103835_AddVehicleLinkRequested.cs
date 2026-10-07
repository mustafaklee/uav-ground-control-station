using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gcs.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVehicleLinkRequested : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "link_requested",
                schema: "gcs",
                table: "vehicles",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "link_requested",
                schema: "gcs",
                table: "vehicles");
        }
    }
}
