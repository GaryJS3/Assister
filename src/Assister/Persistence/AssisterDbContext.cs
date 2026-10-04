using Microsoft.EntityFrameworkCore;

namespace Assister.Persistence;

public sealed class AssisterDbContext(DbContextOptions<AssisterDbContext> Options) : DbContext(Options)
{
    public DbSet<Installation> Installations => Set<Installation>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationTurn> ConversationTurns => Set<ConversationTurn>();

    protected override void OnModelCreating(ModelBuilder ModelBuilder)
    {
        ModelBuilder.Entity<Conversation>().HasIndex(Row => Row.SatelliteId);
        ModelBuilder.Entity<ConversationTurn>().HasIndex(Row => new { Row.ConversationId, Row.Id });
        ModelBuilder.Entity<ConversationTurn>().HasOne<Conversation>().WithMany().HasForeignKey(Row => Row.ConversationId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class Conversation
{
    public Guid Id { get; set; }
    public string SatelliteId { get; set; } = "";
    public long UpdatedAt { get; set; }
    public string Summary { get; set; } = "";
}

public sealed class ConversationTurn
{
    public long Id { get; set; }
    public Guid ConversationId { get; set; }
    public string UserText { get; set; } = "";
    public string AssistantText { get; set; } = "";
    public string Outcome { get; set; } = "";
    public Guid TraceId { get; set; }
}

public sealed class Installation
{
    public int Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
