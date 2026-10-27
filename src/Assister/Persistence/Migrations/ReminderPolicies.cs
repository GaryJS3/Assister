using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Assister.Persistence.Migrations;

[DbContext(typeof(AssisterDbContext))]
[Migration("202610270001_ReminderPolicies")]
public sealed class ReminderPolicies : Migration
{
    protected override void Up(MigrationBuilder Builder) => Builder.Sql("""
        CREATE TABLE ReminderPolicies(ReminderId TEXT PRIMARY KEY REFERENCES Reminders(Id) ON DELETE CASCADE,
            OneTime INTEGER NOT NULL DEFAULT 0, Importance TEXT NOT NULL DEFAULT 'acknowledge');
        """);
    protected override void Down(MigrationBuilder Builder) => Builder.DropTable("ReminderPolicies");
}
