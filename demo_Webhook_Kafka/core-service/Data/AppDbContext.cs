using core_service.Models;
using Microsoft.EntityFrameworkCore;

namespace core_service.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<EventRecord> EventRecords { get; set; }
    public DbSet<EventProcessingStatus> EventProcessingStatuses { get; set; }
    public DbSet<SenderBlacklistEntry> SenderBlacklistEntries { get; set; }
    public DbSet<ModerationReviewItem> ModerationReviewItems { get; set; }
    public DbSet<CommentRecord> CommentRecords { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        modelBuilder.Entity<EventRecord>()
            .HasIndex(e => e.EventId);
        
            modelBuilder.Entity<EventProcessingStatus>()
                .HasIndex(e => e.EventRecordId);

        modelBuilder.Entity<SenderBlacklistEntry>()
            .HasIndex(e => e.SenderId)
            .IsUnique();

        modelBuilder.Entity<ModerationReviewItem>()
            .HasIndex(e => e.EventId);

        modelBuilder.Entity<ModerationReviewItem>()
            .HasIndex(e => e.SenderId);

        modelBuilder.Entity<CommentRecord>()
            .ToTable("comments");

        modelBuilder.Entity<CommentRecord>()
            .Property(e => e.Id)
            .HasColumnName("id");

        modelBuilder.Entity<CommentRecord>()
            .Property(e => e.CommentId)
            .HasColumnName("comment_id")
            .HasMaxLength(100);

        modelBuilder.Entity<CommentRecord>()
            .Property(e => e.PostId)
            .HasColumnName("post_id");

        modelBuilder.Entity<CommentRecord>()
            .Property(e => e.Message)
            .HasColumnName("message");

        modelBuilder.Entity<CommentRecord>()
            .Property(e => e.Intent)
            .HasColumnName("intent");

        modelBuilder.Entity<CommentRecord>()
            .Property(e => e.Sentiment)
            .HasColumnName("sentiment");

        modelBuilder.Entity<CommentRecord>()
            .Property(e => e.Status)
            .HasColumnName("status")
            .HasMaxLength(20)
            .HasDefaultValue("received");

        modelBuilder.Entity<CommentRecord>()
            .Property(e => e.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        modelBuilder.Entity<CommentRecord>()
            .HasIndex(e => e.CommentId)
            .IsUnique();
    }
}
