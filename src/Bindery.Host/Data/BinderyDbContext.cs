using Microsoft.EntityFrameworkCore;

namespace Bindery.Host.Data;

public class BinderyDbContext(DbContextOptions<BinderyDbContext> options) : DbContext(options)
{
    public DbSet<BookEntity> Books => Set<BookEntity>();

    public DbSet<AuthorEntity> Authors => Set<AuthorEntity>();

    public DbSet<TagEntity> Tags => Set<TagEntity>();

    public DbSet<SeriesEntity> Series => Set<SeriesEntity>();

    public DbSet<BookFileEntity> BookFiles => Set<BookFileEntity>();

    public DbSet<BookAuthorEntity> BookAuthors => Set<BookAuthorEntity>();

    public DbSet<BookTagEntity> BookTags => Set<BookTagEntity>();

    public DbSet<DownloadJobEntity> Jobs => Set<DownloadJobEntity>();

    public DbSet<JobLogEntity> JobLog => Set<JobLogEntity>();

    public DbSet<PluginSettingEntity> PluginSettings => Set<PluginSettingEntity>();

    public DbSet<FeedTokenEntity> FeedTokens => Set<FeedTokenEntity>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<BookEntity>(book =>
        {
            book.HasIndex(b => b.SortTitle);
            book.HasIndex(b => b.AddedAt);
            // Duplicate detection on re-download: one book per source identity.
            book.HasIndex(b => new { b.SourcePlugin, b.SourceId });
            book.HasIndex(b => b.DirectoryPath).IsUnique();

            book.HasOne(b => b.Series)
                .WithMany(s => s.Books)
                .HasForeignKey(b => b.SeriesId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        model.Entity<AuthorEntity>(author =>
        {
            author.HasIndex(a => a.Name).IsUnique();
            author.HasIndex(a => a.SortName);
        });

        model.Entity<TagEntity>(tag => tag.HasIndex(t => t.Name).IsUnique());

        model.Entity<SeriesEntity>(series => series.HasIndex(s => s.Name).IsUnique());

        model.Entity<BookAuthorEntity>(join =>
        {
            join.HasKey(ba => new { ba.BookId, ba.AuthorId });

            join.HasOne(ba => ba.Book)
                .WithMany(b => b.Authors)
                .HasForeignKey(ba => ba.BookId)
                .OnDelete(DeleteBehavior.Cascade);

            join.HasOne(ba => ba.Author)
                .WithMany(a => a.Books)
                .HasForeignKey(ba => ba.AuthorId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<BookTagEntity>(join =>
        {
            join.HasKey(bt => new { bt.BookId, bt.TagId });

            join.HasOne(bt => bt.Book)
                .WithMany(b => b.Tags)
                .HasForeignKey(bt => bt.BookId)
                .OnDelete(DeleteBehavior.Cascade);

            join.HasOne(bt => bt.Tag)
                .WithMany(t => t.Books)
                .HasForeignKey(bt => bt.TagId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<BookFileEntity>(file =>
        {
            file.HasIndex(f => new { f.BookId, f.Format }).IsUnique();

            file.HasOne(f => f.Book)
                .WithMany(b => b.Files)
                .HasForeignKey(f => f.BookId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<DownloadJobEntity>(job =>
        {
            job.HasIndex(j => j.Status);
            job.HasIndex(j => j.CreatedAt);

            // Losing the book must not lose the record that it was downloaded.
            job.HasOne(j => j.Book)
                .WithMany()
                .HasForeignKey(j => j.BookId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        model.Entity<JobLogEntity>(entry =>
        {
            entry.HasIndex(e => new { e.JobId, e.At });

            entry.HasOne(e => e.Job)
                .WithMany(j => j.Log)
                .HasForeignKey(e => e.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<PluginSettingEntity>(setting =>
            setting.HasIndex(s => new { s.Plugin, s.Key }).IsUnique());

        model.Entity<FeedTokenEntity>(token =>
        {
            token.HasIndex(t => t.TokenHash).IsUnique();
            token.Ignore(t => t.IsActive);
        });

        base.OnModelCreating(model);
    }
}
