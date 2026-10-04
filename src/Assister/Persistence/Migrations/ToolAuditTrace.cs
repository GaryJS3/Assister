using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Assister.Persistence.Migrations;

[DbContext(typeof(AssisterDbContext))]
[Migration("202610040003_ToolAuditTrace")]
public sealed class ToolAuditTrace : Migration
{
    protected override void Up(MigrationBuilder MigrationBuilder) => MigrationBuilder.Sql("ALTER TABLE ToolAudit ADD COLUMN TraceId TEXT NOT NULL DEFAULT ''; CREATE INDEX IX_ToolAudit_TraceId ON ToolAudit(TraceId);");
    protected override void Down(MigrationBuilder MigrationBuilder) => MigrationBuilder.Sql("DROP INDEX IX_ToolAudit_TraceId; ALTER TABLE ToolAudit DROP COLUMN TraceId;");
}
