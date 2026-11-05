using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Assister.Persistence.Migrations;

[DbContext(typeof(AssisterDbContext))]
[Migration("202610260001_TimerDeviceGroups")]
public sealed class TimerDeviceGroups : Migration
{
    protected override void Up(MigrationBuilder Builder) => Builder.Sql("""
        CREATE TABLE TimerDeviceGroups(TimerId TEXT PRIMARY KEY, Owner TEXT NOT NULL, GroupName TEXT NOT NULL);
        """);
    protected override void Down(MigrationBuilder Builder) => Builder.DropTable("TimerDeviceGroups");
}
