using Assister.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assister.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task MigrationCanRunAgainAndRetainsData()
    {
        var PathName = Path.Combine(Path.GetTempPath(), $"assister-{Guid.NewGuid()}.db");
        var Options = new DbContextOptionsBuilder<AssisterDbContext>().UseSqlite($"Data Source={PathName}").Options;
        await using (var Database = new AssisterDbContext(Options))
        {
            await Database.Database.MigrateAsync();
            Database.Installations.Add(new Installation { CreatedAt = DateTimeOffset.UtcNow });
            await Database.SaveChangesAsync();
        }
        await using (var Database = new AssisterDbContext(Options))
        {
            await Database.Database.MigrateAsync();
            Assert.Single(await Database.Installations.ToListAsync());
        }
    }
}
