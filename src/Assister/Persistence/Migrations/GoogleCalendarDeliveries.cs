using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Assister.Persistence.Migrations;

[DbContext(typeof(AssisterDbContext))]
[Migration("202611040001_GoogleCalendarDeliveries")]
public sealed class GoogleCalendarDeliveries : Migration
{
    protected override void Up(MigrationBuilder Builder) => Builder.Sql("""
        CREATE TABLE GoogleCalendarDeliveries (
            DeliveryKey TEXT NOT NULL PRIMARY KEY, ExpiresAt INTEGER NOT NULL);
        """);
    protected override void Down(MigrationBuilder Builder) => Builder.DropTable("GoogleCalendarDeliveries");
}
