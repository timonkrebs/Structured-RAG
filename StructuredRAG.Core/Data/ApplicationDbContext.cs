using Microsoft.EntityFrameworkCore;
using StructuredRAG.Core.Models;

namespace StructuredRAG.Core.Data;

/// <summary>
/// Database context for the Structured RAG application
/// </summary>
public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Entity> Entities { get; set; }
    public DbSet<Tag> Tags { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Entity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("GETUTCDATE()");

            entity.HasMany(e => e.Tags)
                .WithOne(t => t.Entity)
                .HasForeignKey(t => t.EntityId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Tag>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Name).IsRequired().HasMaxLength(500);
            entity.Property(t => t.Description).HasMaxLength(500);
            entity.Property(t => t.CreatedAt).HasDefaultValueSql("GETUTCDATE()");

            entity.HasIndex(t => t.Name);
        });
    }
}