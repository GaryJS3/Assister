using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Assister.Persistence.Migrations;

[DbContext(typeof(AssisterDbContext))]
[Migration("202610040004_Satellites")]
public sealed class Satellites : Migration
{
    protected override void Up(MigrationBuilder MigrationBuilder) => MigrationBuilder.Sql("""
        CREATE TABLE Satellites (
            Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, AreaId TEXT NULL,
            ProviderType TEXT NOT NULL, Endpoint TEXT NOT NULL, Enabled INTEGER NOT NULL,
            Configuration TEXT NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
        """);
    protected override void Down(MigrationBuilder MigrationBuilder) => MigrationBuilder.DropTable("Satellites");
}
