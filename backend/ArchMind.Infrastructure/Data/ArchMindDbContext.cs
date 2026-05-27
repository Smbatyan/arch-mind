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
    public DbSet<WorkspaceApiKey> WorkspaceApiKeys => Set<WorkspaceApiKey>();
    public DbSet<McpTelemetryEntry> McpTelemetry => Set<McpTelemetryEntry>();
    public DbSet<LlmCallLog> LlmCallLogs => Set<LlmCallLog>();
    public DbSet<Skill> Skills => Set<Skill>();
    public DbSet<SkillRevision> SkillRevisions => Set<SkillRevision>();
    public DbSet<Clarification> Clarifications => Set<Clarification>();

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
            b.Property(x => x.Name)
                .HasColumnName("name")
                .HasMaxLength(200)
                .HasDefaultValue(string.Empty)
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

        // BE-028: bearer tokens used by external MCP clients to authenticate
        // against a workspace. Plaintext is never stored; we keep a SHA-256
        // hash and the first 8 chars of the plaintext for display.
        modelBuilder.Entity<WorkspaceApiKey>(b =>
        {
            b.ToTable("workspace_api_keys");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("uuid")
                .HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.WorkspaceId)
                .HasColumnName("workspace_id")
                .HasColumnType("uuid")
                .IsRequired();
            b.Property(x => x.Name)
                .HasColumnName("name")
                .HasMaxLength(200)
                .IsRequired();
            b.Property(x => x.KeyHash)
                .HasColumnName("key_hash")
                .HasMaxLength(64)
                .IsRequired();
            b.Property(x => x.KeyPrefix)
                .HasColumnName("key_prefix")
                .HasMaxLength(16)
                .IsRequired();
            b.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .HasDefaultValueSql("now()");
            b.Property(x => x.LastUsedAt)
                .HasColumnName("last_used_at")
                .HasColumnType("timestamp with time zone");
            b.Property(x => x.RevokedAt)
                .HasColumnName("revoked_at")
                .HasColumnType("timestamp with time zone");
            b.HasIndex(x => new { x.WorkspaceId, x.RevokedAt })
                .HasDatabaseName("ix_workspace_api_keys_workspace_revoked");
            b.HasIndex(x => x.KeyHash)
                .HasDatabaseName("ix_workspace_api_keys_key_hash")
                .IsUnique();
            b.HasOne(x => x.Workspace)
                .WithMany()
                .HasForeignKey(x => x.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // BE-032: one row per inbound MCP request.
        modelBuilder.Entity<McpTelemetryEntry>(b =>
        {
            b.ToTable("mcp_telemetry");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("uuid")
                .HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.WorkspaceId)
                .HasColumnName("workspace_id")
                .HasColumnType("uuid")
                .IsRequired();
            b.Property(x => x.ApiKeyId)
                .HasColumnName("api_key_id")
                .HasColumnType("uuid");
            b.Property(x => x.Method)
                .HasColumnName("method")
                .HasMaxLength(200)
                .IsRequired();
            b.Property(x => x.StatusCode)
                .HasColumnName("status_code")
                .IsRequired();
            b.Property(x => x.LatencyMs)
                .HasColumnName("latency_ms")
                .IsRequired();
            b.Property(x => x.RequestSizeBytes)
                .HasColumnName("request_size_bytes");
            b.Property(x => x.ResponseSizeBytes)
                .HasColumnName("response_size_bytes");
            b.Property(x => x.ErrorMessage)
                .HasColumnName("error_message")
                .HasMaxLength(4000);
            b.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .HasDefaultValueSql("now()");
            b.HasIndex(x => new { x.WorkspaceId, x.CreatedAt })
                .HasDatabaseName("ix_mcp_telemetry_workspace_created_desc")
                .IsDescending(false, true);
            b.HasIndex(x => new { x.WorkspaceId, x.Method })
                .HasDatabaseName("ix_mcp_telemetry_workspace_method");
            b.HasOne(x => x.Workspace)
                .WithMany()
                .HasForeignKey(x => x.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // BE-032: one row per outbound Anthropic API call.
        modelBuilder.Entity<LlmCallLog>(b =>
        {
            b.ToTable("llm_call_logs");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("uuid")
                .HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.WorkspaceId)
                .HasColumnName("workspace_id")
                .HasColumnType("uuid")
                .IsRequired();
            b.Property(x => x.Purpose)
                .HasColumnName("purpose")
                .HasMaxLength(100)
                .IsRequired();
            b.Property(x => x.Model)
                .HasColumnName("model")
                .HasMaxLength(100)
                .IsRequired();
            b.Property(x => x.InputTokens)
                .HasColumnName("input_tokens")
                .HasDefaultValue(0);
            b.Property(x => x.OutputTokens)
                .HasColumnName("output_tokens")
                .HasDefaultValue(0);
            b.Property(x => x.CacheReadTokens)
                .HasColumnName("cache_read_tokens")
                .HasDefaultValue(0);
            b.Property(x => x.CacheWriteTokens)
                .HasColumnName("cache_write_tokens")
                .HasDefaultValue(0);
            b.Property(x => x.CostUsd)
                .HasColumnName("cost_usd")
                .HasColumnType("numeric(12,6)")
                .HasDefaultValue(0m);
            b.Property(x => x.LatencyMs)
                .HasColumnName("latency_ms")
                .IsRequired();
            b.Property(x => x.CacheHit)
                .HasColumnName("cache_hit")
                .HasDefaultValue(false);
            b.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .HasDefaultValueSql("now()");
            b.HasIndex(x => new { x.WorkspaceId, x.CreatedAt })
                .HasDatabaseName("ix_llm_call_logs_workspace_created_desc")
                .IsDescending(false, true);
            b.HasIndex(x => new { x.WorkspaceId, x.Purpose })
                .HasDatabaseName("ix_llm_call_logs_workspace_purpose");
            b.HasOne(x => x.Workspace)
                .WithMany()
                .HasForeignKey(x => x.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // BE-033: user-authored skills returned by the MCP get_relevant_context
        // tool when their triggers match an agent task. Triggers are stored as
        // a native Postgres text[] (Npgsql maps string[] → text[] by default).
        modelBuilder.Entity<Skill>(b =>
        {
            b.ToTable("skills");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("uuid")
                .HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.WorkspaceId)
                .HasColumnName("workspace_id")
                .HasColumnType("uuid")
                .IsRequired();
            b.Property(x => x.Name)
                .HasColumnName("name")
                .HasMaxLength(64)
                .IsRequired();
            b.Property(x => x.Title)
                .HasColumnName("title")
                .HasMaxLength(200)
                .IsRequired();
            b.Property(x => x.Description)
                .HasColumnName("description")
                .HasMaxLength(2000)
                .IsRequired();
            b.Property(x => x.Body)
                .HasColumnName("body")
                .HasColumnType("text")
                .IsRequired();
            b.Property(x => x.Triggers)
                .HasColumnName("triggers")
                .HasColumnType("text[]")
                .IsRequired();
            b.Property(x => x.Enabled)
                .HasColumnName("enabled")
                .HasDefaultValue(true);
            b.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .HasDefaultValueSql("now()");
            b.HasIndex(x => new { x.WorkspaceId, x.Name })
                .HasDatabaseName("ix_skills_workspace_name")
                .IsUnique();
            b.HasOne(x => x.Workspace)
                .WithMany()
                .HasForeignKey(x => x.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // BE-033: append-only revision history. (SkillId, Version) is unique so
        // concurrent updates that race the version counter would fail-fast on
        // the constraint rather than silently overwrite history.
        modelBuilder.Entity<SkillRevision>(b =>
        {
            b.ToTable("skill_revisions");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("uuid")
                .HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.SkillId)
                .HasColumnName("skill_id")
                .HasColumnType("uuid")
                .IsRequired();
            b.Property(x => x.WorkspaceId)
                .HasColumnName("workspace_id")
                .HasColumnType("uuid")
                .IsRequired();
            b.Property(x => x.Version)
                .HasColumnName("version")
                .IsRequired();
            b.Property(x => x.Title)
                .HasColumnName("title")
                .HasMaxLength(200)
                .IsRequired();
            b.Property(x => x.Description)
                .HasColumnName("description")
                .HasMaxLength(2000)
                .IsRequired();
            b.Property(x => x.Body)
                .HasColumnName("body")
                .HasColumnType("text")
                .IsRequired();
            b.Property(x => x.Triggers)
                .HasColumnName("triggers")
                .HasColumnType("text[]")
                .IsRequired();
            b.Property(x => x.Enabled)
                .HasColumnName("enabled")
                .IsRequired();
            b.Property(x => x.ChangeNote)
                .HasColumnName("change_note")
                .HasMaxLength(2000)
                .IsRequired();
            b.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .HasDefaultValueSql("now()");
            b.HasIndex(x => new { x.SkillId, x.Version })
                .HasDatabaseName("ix_skill_revisions_skill_version")
                .IsUnique();
            b.HasIndex(x => new { x.WorkspaceId, x.CreatedAt })
                .HasDatabaseName("ix_skill_revisions_workspace_created_desc")
                .IsDescending(false, true);
            b.HasOne(x => x.Skill)
                .WithMany()
                .HasForeignKey(x => x.SkillId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.Workspace)
                .WithMany()
                .HasForeignKey(x => x.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // BE-037 (Sprint 5): clarifications — open questions surfaced to a
        // human reviewer when extraction or cross-file correlation can't
        // decide on its own. Enum columns stored as strings; text[] for
        // Choices/RelatedFilePaths/RelatedNodeNames; partial unique index on
        // (workspace_id, fingerprint) WHERE fingerprint IS NOT NULL so we can
        // dedupe candidates without disallowing the null sentinel.
        modelBuilder.Entity<Clarification>(b =>
        {
            b.ToTable("clarifications");
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
                .HasColumnType("uuid");
            b.Property(x => x.Source)
                .HasColumnName("source")
                .HasMaxLength(50)
                .HasConversion<string>()
                .IsRequired();
            b.Property(x => x.Topic)
                .HasColumnName("topic")
                .HasMaxLength(200)
                .IsRequired();
            b.Property(x => x.Question)
                .HasColumnName("question")
                .HasMaxLength(2000)
                .IsRequired();
            b.Property(x => x.Context)
                .HasColumnName("context")
                .HasColumnType("text");
            b.Property(x => x.Choices)
                .HasColumnName("choices")
                .HasColumnType("text[]")
                .IsRequired();
            b.Property(x => x.Priority)
                .HasColumnName("priority")
                .HasDefaultValue(50)
                .IsRequired();
            b.Property(x => x.Status)
                .HasColumnName("status")
                .HasMaxLength(50)
                .HasConversion<string>()
                .HasDefaultValue(ClarificationStatus.Open)
                .IsRequired();
            b.Property(x => x.Answer)
                .HasColumnName("answer")
                .HasColumnType("text");
            b.Property(x => x.AnsweredByUserId)
                .HasColumnName("answered_by_user_id")
                .HasMaxLength(320);
            b.Property(x => x.AnsweredAt)
                .HasColumnName("answered_at")
                .HasColumnType("timestamp with time zone");
            b.Property(x => x.RelatedFilePaths)
                .HasColumnName("related_file_paths")
                .HasColumnType("text[]")
                .IsRequired();
            b.Property(x => x.RelatedNodeNames)
                .HasColumnName("related_node_names")
                .HasColumnType("text[]")
                .IsRequired();
            b.Property(x => x.Fingerprint)
                .HasColumnName("fingerprint")
                .HasMaxLength(128);
            b.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .HasDefaultValueSql("now()");
            b.HasIndex(x => new { x.WorkspaceId, x.Status, x.Priority })
                .HasDatabaseName("ix_clarifications_workspace_status_priority_desc")
                .IsDescending(false, false, true);
            b.HasIndex(x => new { x.WorkspaceId, x.Fingerprint })
                .HasDatabaseName("ix_clarifications_workspace_fingerprint")
                .IsUnique()
                .HasFilter("fingerprint IS NOT NULL");
            b.HasOne<Workspace>()
                .WithMany()
                .HasForeignKey(x => x.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Repo>()
                .WithMany()
                .HasForeignKey(x => x.RepoId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
