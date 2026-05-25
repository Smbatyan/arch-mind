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
    }
}
