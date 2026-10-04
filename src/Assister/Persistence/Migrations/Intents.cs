using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Assister.Persistence.Migrations;

[DbContext(typeof(AssisterDbContext))]
[Migration("202610040006_Intents")]
public sealed class Intents : Migration
{
    protected override void Up(MigrationBuilder MigrationBuilder) => MigrationBuilder.Sql("""
        CREATE TABLE IntentDefinitions (Id TEXT NOT NULL PRIMARY KEY, Payload TEXT NOT NULL, Version INTEGER NOT NULL);
        CREATE TABLE IntentExamples (Id TEXT NOT NULL PRIMARY KEY, Payload TEXT NOT NULL);
        """);
    protected override void Down(MigrationBuilder MigrationBuilder)
    {
        MigrationBuilder.DropTable("IntentExamples");
        MigrationBuilder.DropTable("IntentDefinitions");
    }
}
