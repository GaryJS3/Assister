using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Assister.Persistence.Migrations;

// Checkpoint the contract upgrade so the existing startup flow backs up the database
// before IntentStore upgrades persisted JSON definitions. No table changes are needed.
[DbContext(typeof(AssisterDbContext))]
[Migration("202610040007_IntentActionIds")]
public sealed class IntentActionIds : Migration
{
    protected override void Up(MigrationBuilder MigrationBuilder) { }
    protected override void Down(MigrationBuilder MigrationBuilder) { }
}
