using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Assister.Persistence.Migrations;

[DbContext(typeof(AssisterDbContext))]
public sealed class AssisterDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder ModelBuilder)
    {
        ModelBuilder.HasAnnotation("ProductVersion", "10.0.10");
        ModelBuilder.Entity<Installation>(Entity =>
        {
            Entity.Property(Row => Row.Id).ValueGeneratedOnAdd().HasColumnType("INTEGER");
            Entity.Property(Row => Row.CreatedAt).HasColumnType("TEXT");
            Entity.HasKey(Row => Row.Id);
            Entity.ToTable("Installations");
        });
    }
}
