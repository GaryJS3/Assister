using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Assister.Persistence.Migrations;

[DbContext(typeof(AssisterDbContext))]
[Migration("202610050001_DeviceConversationContext")]
public sealed class DeviceConversationContext : Migration
{
    protected override void Up(MigrationBuilder MigrationBuilder) => MigrationBuilder.Sql(
        "ALTER TABLE Conversations ADD COLUMN DeviceContextJson TEXT NOT NULL DEFAULT '{}';");
    protected override void Down(MigrationBuilder MigrationBuilder) => MigrationBuilder.DropColumn("DeviceContextJson", "Conversations");
}
