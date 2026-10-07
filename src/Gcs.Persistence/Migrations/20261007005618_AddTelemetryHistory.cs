using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Gcs.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTelemetryHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "telemetry_samples",
                schema: "gcs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    vehicle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    latitude = table.Column<double>(type: "double precision", nullable: true),
                    longitude = table.Column<double>(type: "double precision", nullable: true),
                    altitude_msl = table.Column<double>(type: "double precision", nullable: true),
                    relative_altitude = table.Column<double>(type: "double precision", nullable: true),
                    roll = table.Column<double>(type: "double precision", nullable: true),
                    pitch = table.Column<double>(type: "double precision", nullable: true),
                    yaw = table.Column<double>(type: "double precision", nullable: true),
                    ground_speed = table.Column<double>(type: "double precision", nullable: true),
                    air_speed = table.Column<double>(type: "double precision", nullable: true),
                    climb_rate = table.Column<double>(type: "double precision", nullable: true),
                    heading = table.Column<double>(type: "double precision", nullable: true),
                    battery_voltage = table.Column<double>(type: "double precision", nullable: true),
                    battery_current = table.Column<double>(type: "double precision", nullable: true),
                    battery_remaining_percent = table.Column<int>(type: "integer", nullable: true),
                    gps_fix = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    satellites_visible = table.Column<int>(type: "integer", nullable: true),
                    armed = table.Column<bool>(type: "boolean", nullable: true),
                    flight_mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_telemetry_samples", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_samples_time",
                schema: "gcs",
                table: "telemetry_samples",
                column: "recorded_at");

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_samples_vehicle_time",
                schema: "gcs",
                table: "telemetry_samples",
                columns: new[] { "vehicle_id", "recorded_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "telemetry_samples",
                schema: "gcs");
        }
    }
}
