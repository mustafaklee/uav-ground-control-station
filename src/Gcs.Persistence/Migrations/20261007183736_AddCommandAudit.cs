using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gcs.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCommandAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "command_audit",
                schema: "gcs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    vehicle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    callsign = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    @operator = table.Column<string>(name: "operator", type: "character varying(64)", maxLength: 64, nullable: false),
                    command = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    parameters = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    detail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_command_audit", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_command_audit_vehicle_time",
                schema: "gcs",
                table: "command_audit",
                columns: new[] { "vehicle_id", "requested_at" });

            // Append-only, enforced by the database itself and not only by the application: a row may be updated once,
            // from Pending to its outcome, and never deleted. Even a bug or a hand-written SQL statement cannot rewrite history.
            migrationBuilder.Sql("""
                CREATE FUNCTION gcs.command_audit_append_only() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'command_audit is append-only: rows cannot be deleted';
                    END IF;
                    IF OLD.outcome <> 'Pending' THEN
                        RAISE EXCEPTION 'command_audit row % is completed and cannot be changed', OLD.id;
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER command_audit_append_only
                    BEFORE UPDATE OR DELETE ON gcs.command_audit
                    FOR EACH ROW EXECUTE FUNCTION gcs.command_audit_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS command_audit_append_only ON gcs.command_audit;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS gcs.command_audit_append_only();");

            migrationBuilder.DropTable(
                name: "command_audit",
                schema: "gcs");
        }
    }
}
