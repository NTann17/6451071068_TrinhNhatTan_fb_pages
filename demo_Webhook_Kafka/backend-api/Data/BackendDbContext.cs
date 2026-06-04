using backend_api.Models;
using Microsoft.EntityFrameworkCore;

namespace backend_api.Data;

public sealed class BackendDbContext : DbContext
{
    public BackendDbContext(DbContextOptions<BackendDbContext> options) : base(options)
    {
    }

    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<IdempotencyKey>(entity =>
        {
            entity.ToTable("idempotency_keys");
            entity.HasKey(x => x.CommandId);
            entity.Property(x => x.CommandId)
                .HasColumnName("command_id")
                .HasMaxLength(100);
            entity.Property(x => x.ProcessedAt)
                .HasColumnName("processed_at");
            entity.Property(x => x.Status)
                .HasColumnName("status")
                .HasMaxLength(20)
                .IsRequired();
        });
    }
}