using ArchMind.Core.Abstractions;
using ArchMind.Core.Entities;
using ArchMind.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArchMind.Infrastructure.Auth;

/// <summary>
/// Seeds a default admin user on startup if the users table is empty.
/// Intended for MVP / dev bootstrap only.
/// </summary>
public static class UserSeeder
{
    public const string DefaultAdminEmail = "admin@archmind.dev";
    public const string DefaultAdminPassword = "changeme";

    /// <summary>
    /// Idempotent: if any user already exists, returns without changes.
    /// </summary>
    public static async Task SeedAsync(
        ArchMindDbContext db,
        IPasswordHasher hasher,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var anyUser = await db.Users.AnyAsync(cancellationToken);
        if (anyUser)
        {
            return;
        }

        var user = new User
        {
            Email = DefaultAdminEmail,
            PasswordHash = hasher.Hash(DefaultAdminPassword),
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Seeded default admin user '{Email}' with default password. CHANGE PASSWORD BEFORE ANY DEPLOYMENT.",
            DefaultAdminEmail);
    }
}
