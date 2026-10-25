using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Assister.Persistence.Migrations;

[DbContext(typeof(AssisterDbContext))]
[Migration("202610250001_ReminderBroadcasts")]
public sealed class ReminderBroadcasts : Migration
{
    protected override void Up(MigrationBuilder Builder) => Builder.Sql("""
        CREATE TABLE ReminderBroadcastDeliveries (
            ReminderId TEXT NOT NULL, DueAt INTEGER NOT NULL, SatelliteId TEXT NOT NULL,
            Delivered INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (ReminderId, DueAt, SatelliteId));
        """);
    protected override void Down(MigrationBuilder Builder) => Builder.DropTable("ReminderBroadcastDeliveries");
}
