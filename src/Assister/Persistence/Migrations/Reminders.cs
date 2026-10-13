using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Assister.Persistence.Migrations;
[DbContext(typeof(AssisterDbContext))]
[Migration("202610120001_Reminders")]
public sealed class Reminders : Migration
{
    protected override void Up(MigrationBuilder Builder) => Builder.Sql("""
        CREATE TABLE Reminders (Id TEXT NOT NULL PRIMARY KEY, Message TEXT NOT NULL, SatelliteId TEXT NOT NULL,
        TimeZone TEXT NOT NULL, Hour INTEGER NOT NULL, Minute INTEGER NOT NULL, Weekday INTEGER,
        NextDueAt INTEGER NOT NULL, Status TEXT NOT NULL DEFAULT 'active');
        CREATE INDEX IX_Reminders_Due ON Reminders(Status,NextDueAt);
        """);
    protected override void Down(MigrationBuilder Builder) => Builder.DropTable("Reminders");
}
