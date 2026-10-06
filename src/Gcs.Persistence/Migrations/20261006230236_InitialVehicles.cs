using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gcs.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialVehicles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "gcs");

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "gcs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vehicles",
                schema: "gcs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    callsign = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    system_id = table.Column<short>(type: "smallint", nullable: false),
                    autopilot = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    connection_baud_rate = table.Column<int>(type: "integer", nullable: true),
                    connection_host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    connection_port = table.Column<int>(type: "integer", nullable: true),
                    connection_serial_port_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    connection_transport = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vehicles", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                schema: "gcs",
                table: "outbox_messages",
                column: "occurred_at",
                filter: "processed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_vehicles_callsign_active",
                schema: "gcs",
                table: "vehicles",
                column: "callsign",
                unique: true,
                filter: "status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ux_vehicles_system_id_active",
                schema: "gcs",
                table: "vehicles",
                column: "system_id",
                unique: true,
                filter: "status = 'Active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "gcs");

            migrationBuilder.DropTable(
                name: "vehicles",
                schema: "gcs");
        }
    }
}
