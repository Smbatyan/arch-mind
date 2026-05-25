using System.Text;
using ArchMind.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace ArchMind.Infrastructure.Data;

public class ArchMindDbContext : DbContext
{
    public ArchMindDbContext(DbContextOptions<ArchMindDbContext> options) : base(options)
    {
    }

    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<User> Users => Set<User>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<Repo> Repos => Set<Repo>();
    public DbSet<LlmExtractionCacheEntry> LlmExtractionCache => Set<LlmExtractionCacheEntry>();
    public DbSet<FileExtraction> FileExtractions => Set<FileExtraction>();
    public DbSet<ScanRun> ScanRuns => Set<ScanRun>();
    public DbSet<CorrelationConflict> CorrelationConflicts => Set<CorrelationConflict>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Workspace
        modelBuilder.Entity<Workspace>(b =>
        {
            b.ToTable("workspaces");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("uuid")
                .HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.Slug)
                .HasColumnName("slug")
                .HasMaxLength(50)
                .IsRequired();
            b.Property(x => x.Name)
                .HasColumnName("name")
                .HasMaxLength(200)
                .IsRequired();
            b.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()");
            b.HasIndex(x => x.Slug).IsUnique();
        });

        // User
        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("users");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("uuid")
                .HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.Email)
                .HasColumnName("email")
                .HasMaxLength(320)
                .IsRequired();
            b.Property(x => x.PasswordHash)
                .HasColumnName("password_hash")
                .IsRequired();
            b.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()");
            b.HasIndex(x => x.Email).IsUnique();
        });

        // WorkspaceMember (composite PK, cascade delete)
        modelBuilder.Entity<WorkspaceMember>(b =>
        {
            b.ToTable("workspace_members");
            b.HasKey(x => new { x.WorkspaceId, x.UserId });
            b.Property(x => x.WorkspaceId)
                .HasColumnName("workspace_id")
                .HasColumnType("uuid");
            b.Property(x => x.UserId)
                .HasColumnName("user_id")
                .HasColumnType("uuid");
            b.Property(x => x.Role)
                .HasColumnName("role")
                .HasMaxLength(50)
                .IsRequired();
            b.HasOne(x => x.Workspace)
                .WithMany(w => w.Members)
                .HasForeignKey(x => x.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.User)
                .WithMany(u => u.Memberships)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Repo
        modelBuilder.Entity<Repo>(b =>
        {
            b.ToTable("repos");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("uuid")
                .HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.WorkspaceId)
                .HasColumnName("workspace_id")
                .HasColumnType("uuid")
                .IsRequired();
            b.Property(x => x.GitHubUrl)
                .HasColumnName("github_url")
                .HasMaxLength(500)
                .IsRequired();
            b.Property(x => x.DefaultBranch)
                .HasColumnName("default_branch")
                .HasMaxLength(200)
                .HasDefaultValue("main")
                .IsRequired();
            b.Property(x => x.LastProcessedSha)
                .HasColumnName("last_processed_sha")
                .HasMaxLength(64);
            b.Property(x => x.WorkingDirPath)
                .HasColumnName("working_dir_path")
                .HasMaxLength(500)
                .IsRequired();
            b.Property(x => x.PatToken)
                .HasColumnName("pat_token")
                .HasMaxLength(500)
                .IsRequired();
            b.Property(x => x.Status)
                .HasColumnName("status")
                .HasMaxLength(50)
                .HasDefaultValue("pending")
                .IsRequired();
            b.Property(x => x.ErrorMessage)
                .HasColumnName("error_message")
                .HasMaxLength(2000);
            b.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt)
                .HasColumnName("updated_at")
                .HasDefaultValueSql("now()");
            b.HasIndex(x => x.WorkspaceId);
            b.HasOne(x => x.Workspace)
                .WithMany()
                .HasForeignKey(x => x.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // LlmExtractionCacheEntry
        modelBuilder.Entity<LlmExtractionCacheEntry>(b =>
        {
            b.ToTable("llm_extraction_cache");
            b.HasKey(x => x.ContentHash);
            b.Property(x => x.ContentHash)
                .HasColumnName("content_hash")
                .HasMaxLength(64)
                .IsRequired();
            b.Property(x => x.WorkspaceId)
                .HasColumnName("workspace_id")
                .HasColumnType("uuid")
                .IsRequired();
            b.Property(x => x.Model)
                .HasColumnName("model")
                .HasMaxLength(50)
                .IsRequired();
            b.Property(x => x.PromptVersion)
                .HasColumnName("prompt_version")
                .HasMaxLength(50)
                .IsRequired();
            b.Property(x => x.Result)
                .HasColumnName("result")
                .HasColumnType("jsonb")
                .IsRequired();
            b.Property(x => x.HitCount)
                .HasColumnName("hit_count")
                .HasDefaultValue(0);
            b.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()");
            b.HasIndex(x => x.WorkspaceId);
            b.HasOne(x => x.Workspace)
                .WithMany()
                .HasForeignKey(x => x.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // FileExtraction (BE-018): aggregated per-file extraction payload.
        modelBuilder.Entity<FileExtraction>(b =>
        {
            b.ToTable("file_extractions");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("uuid")
                .HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.WorkspaceId)
                .HasColumnName("workspace_id")
                .HasColumnType("uuid")
                .IsRequired();
            b.Property(x => x.RepoId)
                .HasColumnName("repo_id")
                .HasColumnType("uuid")
                .IsRequired();
            b.Property(x => x.FilePath)
                .HasColumnName("file_path")
                .HasMaxLength(1000)
                .IsRequired();
            b.Property(x => x.ContentHash)
                .HasColumnName("content_hash")
                .HasMaxLength(64)
                .IsRequired();
            b.Property(x => x.ExtractionPayload)
                .HasColumnName("extraction_payload")
                .HasColumnType("jsonb")
                .IsRequired();
            b.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()");
            b.HasIndex(x => new { x.WorkspaceId, x.RepoId, x.FilePath }).IsUnique();
            b.HasIndex(x => x.ContentHash);
            b.HasOne(x => x.Workspace)
                .WithMany()
                .HasForeignKey(x => x.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ScanRun (BE-019): per-scan telemetry row.
        modelBuilder.Entity<ScanRun>(b =>
        {
            b.ToTable("scan_runs");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("uuid")
                .HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.WorkspaceId)
                .HasColumnName("workspace_id")
                .HasColumnType("uuid")
                .IsRequired();
            b.Property(x => x.RepoId)
                .HasColumnName("repo_id")
                .HasColumnType("uuid")
                .IsRequired();
            b.Property(x => x.Kind)
                .HasColumnName("kind")
                .HasMaxLength(20)
                .IsRequired();
            b.Property(x => x.Status)
                .HasColumnName("status")
                .HasMaxLength(20)
                .IsRequired();
            b.Property(x => x.StartedAt)
                .HasColumnName("started_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            b.Property(x => x.CompletedAt)
                .HasColumnName("completed_at")
                .HasColumnType("timestamp with time zone");
            b.Property(x => x.FromSha)
                .HasColumnName("from_sha")
                .HasMaxLength(64);
            b.Property(x => x.ToSha)
                .HasColumnName("to_sha")
                .HasMaxLength(64);
            b.Property(x => x.FilesScanned)
                .HasColumnName("files_scanned")
                .HasDefaultValue(0);
            b.Property(x => x.FilesEnqueued)
                .HasColumnName("files_enqueued")
                .HasDefaultValue(0);
            b.Property(x => x.GraphifyNodes)
                .HasColumnName("graphify_nodes")
                .HasDefaultValue(0);
            b.Property(x => x.GraphifyEdges)
                .HasColumnName("graphify_edges")
                .HasDefaultValue(0);
            b.Property(x => x.TotalTokens)
                .HasColumnName("total_tokens")
                .HasDefaultValue(0L);
            b.Property(x => x.TotalCostUsd)
                .HasColumnName("total_cost_usd")
                .HasColumnType("numeric(10,6)")
                .HasDefaultValue(0m);
            b.Property(x => x.ErrorMessage)
                .HasColumnName("error_message")
                .HasMaxLength(4000);
            b.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("now()");
            b.HasIndex(x => new { x.WorkspaceId, x.RepoId, x.StartedAt })
                .HasDatabaseName("ix_scan_runs_workspace_repo_started_at_desc")
                .IsDescending(false, false, true);
            b.HasOne(x => x.Workspace)
                .WithMany()
                .HasForeignKey(x => x.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // CorrelationConflict (BE-026): per-conflict row written by the
        // cross-file correlator for the Sprint 5 clarification engine.
        modelBuilder.Entity<CorrelationConflict>(b =>
        {
            b.ToTable("correlation_conflicts");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("uuid")
                .HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.WorkspaceId)
                .HasColumnName("workspace_id")
                .HasColumnType("uuid")
                .IsRequired();
            b.Property(x => x.RepoId)
                .HasColumnName("repo_id")
                .HasColumnType("uuid")
                .IsRequired();
            b.Property(x => x.Kind)
                .HasColumnName("kind")
                .HasMaxLength(50)
                .IsRequired();
            b.Property(x => x.Description)
                .HasColumnName("description")
                .HasMaxLength(2000)
                .IsRequired();
            b.Property(x => x.Involved)
                .HasColumnName("involved")
                .HasColumnType("jsonb")
                .IsRequired();
            b.Property(x => x.Status)
                .HasColumnName("status")
                .HasMaxLength(20)
                .HasDefaultValue("open")
                .IsRequired();
            b.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .HasDefaultValueSql("now()");
            b.HasIndex(x => new { x.WorkspaceId, x.RepoId, x.Status, x.CreatedAt })
                .HasDatabaseName("ix_correlation_conflicts_workspace_repo_status_created_desc")
                .IsDescending(false, false, false, true);
            b.HasOne(x => x.Workspace)
                .WithMany()
                .HasForeignKey(x => x.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
