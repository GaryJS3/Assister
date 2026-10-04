using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Assister.Persistence.Migrations;

[DbContext(typeof(AssisterDbContext))]
[Migration("202610040001_Conversations")]
public sealed class Conversations : Migration
{
    protected override void Up(MigrationBuilder MigrationBuilder)
    {
        MigrationBuilder.Sql("""
            CREATE TABLE Conversations (Id TEXT NOT NULL PRIMARY KEY, SatelliteId TEXT NOT NULL, UpdatedAt INTEGER NOT NULL, Summary TEXT NOT NULL);
            CREATE INDEX IX_Conversations_SatelliteId ON Conversations (SatelliteId);
            CREATE TABLE ConversationTurns (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, ConversationId TEXT NOT NULL, UserText TEXT NOT NULL, AssistantText TEXT NOT NULL, Outcome TEXT NOT NULL, TraceId TEXT NOT NULL, FOREIGN KEY (ConversationId) REFERENCES Conversations(Id) ON DELETE CASCADE);
            CREATE INDEX IX_ConversationTurns_ConversationId_Id ON ConversationTurns (ConversationId, Id);
            """);
    }
    protected override void Down(MigrationBuilder MigrationBuilder)
    {
        MigrationBuilder.DropTable("ConversationTurns");
        MigrationBuilder.DropTable("Conversations");
    }
}
