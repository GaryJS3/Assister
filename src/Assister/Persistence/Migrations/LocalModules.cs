using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Assister.Persistence.Migrations;

[DbContext(typeof(AssisterDbContext))]
[Migration("202610040002_LocalModules")]
public sealed class LocalModules : Migration
{
    protected override void Up(MigrationBuilder MigrationBuilder)
    {
        // These tables are accessed through parameterized commands; FTS5 has no EF entity mapping.
        MigrationBuilder.Sql("""
            CREATE TABLE Timers (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, SatelliteId TEXT NOT NULL, Area TEXT, ConversationId TEXT, CreatedAt INTEGER NOT NULL, DueAt INTEGER NOT NULL, Status TEXT NOT NULL);
            CREATE INDEX IX_Timers_Status_DueAt ON Timers(Status, DueAt);
            CREATE TABLE Memories (Id TEXT NOT NULL PRIMARY KEY, Content TEXT NOT NULL, Subject TEXT NOT NULL, CreatedAt INTEGER NOT NULL, ConversationId TEXT);
            CREATE VIRTUAL TABLE MemorySearch USING fts5(Id UNINDEXED, Content, Subject);
            CREATE TRIGGER Memories_Insert AFTER INSERT ON Memories BEGIN INSERT INTO MemorySearch(Id,Content,Subject) VALUES(new.Id,new.Content,new.Subject); END;
            CREATE TRIGGER Memories_Delete AFTER DELETE ON Memories BEGIN DELETE FROM MemorySearch WHERE Id=old.Id; END;
            CREATE TABLE ToolAudit (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, ConversationId TEXT, SatelliteId TEXT NOT NULL, Tool TEXT NOT NULL, Outcome TEXT NOT NULL, CreatedAt INTEGER NOT NULL);
            """);
    }
    protected override void Down(MigrationBuilder MigrationBuilder)
    {
        foreach (var Table in new[] { "ToolAudit", "MemorySearch", "Memories", "Timers" }) { MigrationBuilder.DropTable(Table); }
    }
}
