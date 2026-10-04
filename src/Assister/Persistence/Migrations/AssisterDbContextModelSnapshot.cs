using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Assister.Persistence.Migrations;

[DbContext(typeof(AssisterDbContext))]
public sealed class AssisterDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder ModelBuilder)
    {
        ModelBuilder.HasAnnotation("ProductVersion", "10.0.10");
        ModelBuilder.Entity<Assister.Intents.IntentDefinitionRow>(Entity =>
        {
            Entity.Property(Row => Row.Id).HasColumnType("TEXT");
            Entity.Property(Row => Row.Payload).IsRequired().HasColumnType("TEXT");
            Entity.Property(Row => Row.Version).IsConcurrencyToken().HasColumnType("INTEGER");
            Entity.HasKey(Row => Row.Id);
            Entity.ToTable("IntentDefinitions");
        });
        ModelBuilder.Entity<Assister.Intents.IntentExampleRow>(Entity =>
        {
            Entity.Property(Row => Row.Id).HasColumnType("TEXT");
            Entity.Property(Row => Row.Payload).IsRequired().HasColumnType("TEXT");
            Entity.HasKey(Row => Row.Id);
            Entity.ToTable("IntentExamples");
        });
        ModelBuilder.Entity<Assister.Satellites.Satellite>(Entity =>
        {
            Entity.Property(Row => Row.Id).HasColumnType("TEXT");
            Entity.Property(Row => Row.Name).IsRequired().HasColumnType("TEXT");
            Entity.Property(Row => Row.AreaId).HasColumnType("TEXT");
            Entity.Property(Row => Row.ProviderType).IsRequired().HasColumnType("TEXT");
            Entity.Property(Row => Row.Endpoint).IsRequired().HasColumnType("TEXT");
            Entity.Property(Row => Row.Enabled).HasColumnType("INTEGER");
            Entity.Property(Row => Row.Configuration).IsRequired().HasColumnType("TEXT");
            Entity.Property(Row => Row.CreatedAt).HasColumnType("TEXT");
            Entity.Property(Row => Row.UpdatedAt).HasColumnType("TEXT");
            Entity.HasKey(Row => Row.Id);
            Entity.ToTable("Satellites");
        });
        ModelBuilder.Entity<Installation>(Entity =>
        {
            Entity.Property(Row => Row.Id).ValueGeneratedOnAdd().HasColumnType("INTEGER");
            Entity.Property(Row => Row.CreatedAt).HasColumnType("TEXT");
            Entity.HasKey(Row => Row.Id);
            Entity.ToTable("Installations");
        });
        ModelBuilder.Entity<Conversation>(Entity =>
        {
            Entity.Property(Row => Row.Id).HasColumnType("TEXT");
            Entity.Property(Row => Row.SatelliteId).IsRequired().HasColumnType("TEXT");
            Entity.Property(Row => Row.UpdatedAt).HasColumnType("INTEGER");
            Entity.Property(Row => Row.Summary).IsRequired().HasColumnType("TEXT");
            Entity.HasKey(Row => Row.Id);
            Entity.HasIndex(Row => Row.SatelliteId);
            Entity.ToTable("Conversations");
        });
        ModelBuilder.Entity<ConversationTurn>(Entity =>
        {
            Entity.Property(Row => Row.Id).ValueGeneratedOnAdd().HasColumnType("INTEGER");
            Entity.Property(Row => Row.ConversationId).HasColumnType("TEXT");
            Entity.Property(Row => Row.UserText).IsRequired().HasColumnType("TEXT");
            Entity.Property(Row => Row.AssistantText).IsRequired().HasColumnType("TEXT");
            Entity.Property(Row => Row.Outcome).IsRequired().HasColumnType("TEXT");
            Entity.Property(Row => Row.TraceId).HasColumnType("TEXT");
            Entity.HasKey(Row => Row.Id);
            Entity.HasIndex(Row => new { Row.ConversationId, Row.Id });
            Entity.HasOne<Conversation>().WithMany().HasForeignKey(Row => Row.ConversationId).OnDelete(DeleteBehavior.Cascade);
            Entity.ToTable("ConversationTurns");
        });
    }
}
