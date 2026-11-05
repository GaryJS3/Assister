using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Assister.Persistence.Migrations;

[DbContext(typeof(AssisterDbContext))]
[Migration("202611040002_GoogleCalendarAgendas")]
public sealed class GoogleCalendarAgendas : Migration
{
    protected override void Up(MigrationBuilder Builder) => Builder.Sql("""
        CREATE TABLE GoogleCalendarAgendas (AgendaKey TEXT NOT NULL PRIMARY KEY, Payload TEXT NOT NULL, ExpiresAt INTEGER NOT NULL);
        """);
    protected override void Down(MigrationBuilder Builder) => Builder.DropTable("GoogleCalendarAgendas");
}
