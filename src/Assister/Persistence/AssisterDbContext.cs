using Microsoft.EntityFrameworkCore;

namespace Assister.Persistence;

public sealed class AssisterDbContext(DbContextOptions<AssisterDbContext> Options) : DbContext(Options)
{
    public DbSet<Installation> Installations => Set<Installation>();
}

public sealed class Installation
{
    public int Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
