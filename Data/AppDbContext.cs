using Microsoft.EntityFrameworkCore;
using StreamApp.Models;

namespace StreamApp.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<SourceDirectory> SourceDirectories => Set<SourceDirectory>();
    public DbSet<Movie> Movies => Set<Movie>();
    public DbSet<Genre> Genres => Set<Genre>();
    public DbSet<MovieGenre> MovieGenres => Set<MovieGenre>();
    public DbSet<MediaFile> MediaFiles => Set<MediaFile>();
    public DbSet<MediaStream> MediaStreams => Set<MediaStream>();
    public DbSet<Subtitle> Subtitles => Set<Subtitle>();
    public DbSet<ScanHistory> ScanHistories => Set<ScanHistory>();
    public DbSet<Series> SeriesEntries => Set<Series>();
    public DbSet<Season> Seasons => Set<Season>();
    public DbSet<Episode> Episodes => Set<Episode>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Category>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(120).IsRequired();
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => new { x.ParentCategoryId, x.DisplayOrder });
            e.HasOne(x => x.Parent).WithMany(x => x.Children)
                .HasForeignKey(x => x.ParentCategoryId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Source>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.RootUrl).HasMaxLength(1000).IsRequired();
            e.Property(x => x.ScanStrategy).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.LastScanStatus).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.ContentType).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => x.RootUrl).IsUnique();
            e.HasOne(x => x.Category).WithMany(x => x.Sources)
                .HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<SourceDirectory>(e =>
        {
            e.Property(x => x.Url).HasMaxLength(1000).IsRequired();
            e.HasIndex(x => new { x.SourceId, x.Url }).IsUnique();
            e.HasOne(x => x.Source).WithMany(x => x.Directories)
                .HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Movie>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(400).IsRequired();
            e.Property(x => x.FolderName).HasMaxLength(500).IsRequired();
            e.Property(x => x.SourceFolderUrl).HasMaxLength(1500).IsRequired();
            e.Property(x => x.MatchStatus).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.ContentType).HasConversion<string>().HasMaxLength(16);
            e.HasOne(x => x.Series).WithOne(x => x.Catalog).HasForeignKey<Series>(x => x.Id).OnDelete(DeleteBehavior.Cascade);
            // One row per source folder. Deliberately NO unique index on TmdbId or Title:
            // copies of the same film from different directories stay independent entries.
            e.HasIndex(x => new { x.SourceId, x.SourceFolderUrl }).IsUnique();
            e.HasIndex(x => x.CategoryId);
            e.HasIndex(x => x.Title);
            e.HasIndex(x => x.ReleaseYear);
            e.HasIndex(x => x.TmdbId);
            e.HasIndex(x => x.CreatedAt);
            e.HasOne(x => x.Category).WithMany(x => x.Movies)
                .HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Source).WithMany(x => x.Movies)
                .HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Series>(e =>
        {
            e.ToTable("Series");
            e.Property(x => x.Id).ValueGeneratedNever();
        });

        b.Entity<Season>(e =>
        {
            e.HasIndex(x => new { x.SeriesId, x.Number }).IsUnique();
            e.HasOne(x => x.Series).WithMany(x => x.Seasons).HasForeignKey(x => x.SeriesId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Episode>(e =>
        {
            e.HasIndex(x => new { x.SeasonId, x.Number }).IsUnique();
            e.HasOne(x => x.Season).WithMany(x => x.Episodes).HasForeignKey(x => x.SeasonId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Genre>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => x.TmdbGenreId).IsUnique();
        });

        b.Entity<MovieGenre>(e =>
        {
            e.HasKey(x => new { x.MovieId, x.GenreId });
            e.HasOne(x => x.Movie).WithMany(x => x.MovieGenres)
                .HasForeignKey(x => x.MovieId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Genre).WithMany(x => x.MovieGenres)
                .HasForeignKey(x => x.GenreId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<MediaFile>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(500).IsRequired();
            e.Property(x => x.FileUrl).HasMaxLength(1500).IsRequired();
            e.HasIndex(x => new { x.MovieId, x.FileUrl }).IsUnique();
            e.HasIndex(x => x.EpisodeId);
            e.HasOne(x => x.Movie).WithMany(x => x.MediaFiles)
                .HasForeignKey(x => x.MovieId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Episode).WithMany(x => x.MediaFiles)
                .HasForeignKey(x => x.EpisodeId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<MediaStream>(e =>
        {
            e.Property(x => x.StreamType).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => x.MediaFileId);
            e.HasOne(x => x.MediaFile).WithMany(x => x.Streams)
                .HasForeignKey(x => x.MediaFileId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Subtitle>(e =>
        {
            e.Property(x => x.FileUrl).HasMaxLength(1500).IsRequired();
            e.HasIndex(x => x.EpisodeId);
            e.HasOne(x => x.Movie).WithMany(x => x.Subtitles)
                .HasForeignKey(x => x.MovieId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Episode).WithMany(x => x.Subtitles)
                .HasForeignKey(x => x.EpisodeId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ScanHistory>(e =>
        {
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => new { x.SourceId, x.StartedAt });
            e.HasOne(x => x.Source).WithMany(x => x.ScanHistories)
                .HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
