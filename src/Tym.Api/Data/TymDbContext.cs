using Microsoft.EntityFrameworkCore;
using Tym.Api.Domain;

namespace Tym.Api.Data;

public sealed class TymDbContext : DbContext
{
    public TymDbContext(DbContextOptions<TymDbContext> options) : base(options)
    {
    }

    public DbSet<TimelineEvent> Events => Set<TimelineEvent>();
    public DbSet<YardLink> YardLinks => Set<YardLink>();
    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TimelineEvent>(entity =>
        {
            entity.ToTable("events");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Subject).HasMaxLength(240);
            entity.Property(e => e.EventType).HasMaxLength(80);
            entity.Property(e => e.Source).HasMaxLength(260);
            entity.Property(e => e.Actor).HasMaxLength(160);
            entity.Property(e => e.StatusBefore).HasMaxLength(80);
            entity.Property(e => e.StatusAfter).HasMaxLength(80);
            entity.Property(e => e.Modality).HasMaxLength(80);
            entity.Property(e => e.MediaUri).HasMaxLength(600);
            entity.Property(e => e.ThumbnailUri).HasMaxLength(600);
            entity.HasIndex(e => e.Subject);
            entity.HasIndex(e => e.TimestampStart);
            entity.HasIndex(e => e.IsSuperseded);
            entity.HasIndex(e => e.Modality);
            entity.HasIndex(e => e.MediaAssetId);
            entity.HasIndex(e => e.MediaStartSeconds);
        });

        modelBuilder.Entity<YardLink>(entity =>
        {
            entity.ToTable("yard_links");
            entity.HasKey(l => l.Id);
            entity.Property(l => l.LinkType).HasMaxLength(80);
            entity.HasIndex(l => l.FromEventId);
            entity.HasIndex(l => l.ToEventId);
            entity.HasIndex(l => l.LinkType);
        });

        modelBuilder.Entity<MediaAsset>(entity =>
        {
            entity.ToTable("media_assets");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.OriginalFileName).HasMaxLength(260);
            entity.Property(a => a.StoredFileName).HasMaxLength(260);
            entity.Property(a => a.ContentType).HasMaxLength(120);
            entity.Property(a => a.Source).HasMaxLength(260);
            entity.Property(a => a.Status).HasMaxLength(80);
            entity.HasIndex(a => a.CreatedAt);
            entity.HasIndex(a => a.Status);
        });
    }
}
