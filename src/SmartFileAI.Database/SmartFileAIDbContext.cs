using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartFileAI.Core.Models.Entities;

namespace SmartFileAI.Database;

public class SmartFileAIDbContext : DbContext
{
    public DbSet<FileItem> Files => Set<FileItem>();
    public DbSet<ScanHistory> ScanHistories => Set<ScanHistory>();
    public DbSet<OperationLog> OperationLogs => Set<OperationLog>();

    public string DbPath { get; }

    public SmartFileAIDbContext(DbContextOptions<SmartFileAIDbContext> options) : base(options)
    {
        DbPath = Database.GetDbConnection().DataSource;
    }

    public SmartFileAIDbContext(string? dbPath = null)
    {
        DbPath = string.IsNullOrWhiteSpace(dbPath) ? "smartfile.db" : dbPath;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (optionsBuilder.IsConfigured) return;

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        };
        optionsBuilder.UseSqlite(csb.ToString());
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<FileItem>(entity =>
        {
            entity.ToTable("Files");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(260);
            entity.Property(e => e.FullPath).IsRequired().UseCollation("NOCASE");
            entity.Property(e => e.ParentPath).IsRequired().UseCollation("NOCASE");
            entity.Property(e => e.Extension).HasMaxLength(64).UseCollation("NOCASE");
            entity.HasIndex(e => e.FullPath).IsUnique().HasDatabaseName("IX_Files_FullPath");
            entity.HasIndex(e => e.Name).HasDatabaseName("IX_Files_Name");
            entity.HasIndex(e => e.Extension).HasDatabaseName("IX_Files_Extension");
            entity.HasIndex(e => e.ParentPath).HasDatabaseName("IX_Files_ParentPath");
            entity.HasIndex(e => e.Size).HasDatabaseName("IX_Files_Size");
        });

        modelBuilder.Entity<ScanHistory>(entity =>
        {
            entity.ToTable("ScanHistories");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Drive).IsRequired().HasMaxLength(1024);
        });

        modelBuilder.Entity<OperationLog>(entity =>
        {
            entity.ToTable("OperationLogs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Path).IsRequired();
            entity.HasIndex(e => e.Time).HasDatabaseName("IX_OperationLogs_Time");
        });
    }

    public async Task InitializeDatabaseAsync(CancellationToken cancellationToken = default)
    {
        await Database.EnsureCreatedAsync(cancellationToken);
        await Database.ExecuteSqlRawAsync(@"
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA temp_store = MEMORY;
            PRAGMA cache_size = -64000;", cancellationToken);
    }
}
