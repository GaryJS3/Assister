using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Assister.Persistence.Migrations;

[DbContext(typeof(AssisterDbContext))]
[Migration("202610030001_InitialSchema")]
public sealed class InitialSchema : Migration
{
    protected override void Up(MigrationBuilder MigrationBuilder)
    {
        MigrationBuilder.CreateTable(
            name: "Installations",
            columns: Table => new
            {
                Id = Table.Column<int>(nullable: false).Annotation("Sqlite:Autoincrement", true),
                CreatedAt = Table.Column<DateTimeOffset>(nullable: false)
            },
            constraints: Table => Table.PrimaryKey("PK_Installations", Row => Row.Id));
    }

    protected override void Down(MigrationBuilder MigrationBuilder)
    {
        MigrationBuilder.DropTable("Installations");
    }
}
