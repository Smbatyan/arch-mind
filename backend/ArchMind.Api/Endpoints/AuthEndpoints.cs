using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ArchMind.Core.Abstractions;
using ArchMind.Core.Entities;
using ArchMind.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace ArchMind.Api.Endpoints;

/// <summary>
/// Minimal API endpoints for custom email/password authentication.
/// Issues stateless JWTs (10-day expiry). The token is returned in the
/// response body AND set as the "archmind.sid" cookie so SSR pages keep
/// working without code changes.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        // TODO: Restrict registration in production (invite-only or admin-created).
        group.MapPost("/register", RegisterAsync).AllowAnonymous();
        group.MapPost("/login", LoginAsync).AllowAnonymous();
        group.MapPost("/logout", LogoutAsync).AllowAnonymous();
        group.MapGet("/me", Me); // requires auth via fallback policy

        return app;
    }

    // ---------------------------------------------------------------------
    // DTOs
    // ---------------------------------------------------------------------
    public sealed record RegisterRequest(string? Email, string? Password);
    public sealed record LoginRequest(string? Email, string? Password);

    // ---------------------------------------------------------------------
    // Handlers
    // ---------------------------------------------------------------------
    private static async Task<IResult> RegisterAsync(
        [FromBody] RegisterRequest req,
        ArchMindDbContext db,
        IPasswordHasher hasher,
        IConfiguration configuration,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var email = req.Email?.Trim();
        var password = req.Password;

        if (string.IsNullOrWhiteSpace(email) || !IsValidEmail(email))
        {
            return Results.BadRequest(new { error = "invalid email" });
        }
        if (string.IsNullOrEmpty(password) || password.Length < 8)
        {
            return Results.BadRequest(new { error = "password must be at least 8 characters" });
        }

        var normalized = email.ToLowerInvariant();
        var exists = await db.Users.AnyAsync(u => u.Email == normalized, ct);
        if (exists)
        {
            return Results.Conflict(new { error = "user already exists" });
        }

        var user = new User
        {
            Email = normalized,
            PasswordHash = hasher.Hash(password),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        var (token, expiresAt) = IssueJwt(user, configuration, httpContext);

        return Results.Ok(new { user = new { id = user.Id, email = user.Email }, token, expiresAt });
    }

    private static async Task<IResult> LoginAsync(
        [FromBody] LoginRequest req,
        ArchMindDbContext db,
        IPasswordHasher hasher,
        IConfiguration configuration,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var email = req.Email?.Trim().ToLowerInvariant();
        var password = req.Password;

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            return Results.Json(new { error = "invalid credentials" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null || !hasher.Verify(password, user.PasswordHash))
        {
            return Results.Json(new { error = "invalid credentials" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var (token, expiresAt) = IssueJwt(user, configuration, httpContext);

        return Results.Ok(new { user = new { id = user.Id, email = user.Email }, token, expiresAt });
    }

    private static IResult LogoutAsync(HttpContext httpContext)
    {
        httpContext.Response.Cookies.Delete("archmind.sid", new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
        });
        return Results.NoContent();
    }

    private static IResult Me(HttpContext httpContext)
    {
        var idClaim = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? httpContext.User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        var emailClaim = httpContext.User.FindFirstValue(ClaimTypes.Email)
            ?? httpContext.User.FindFirstValue(JwtRegisteredClaimNames.Email);
        if (string.IsNullOrEmpty(idClaim) || string.IsNullOrEmpty(emailClaim))
        {
            return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        return Results.Ok(new { user = new { id = idClaim, email = emailClaim } });
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------
    private static (string Token, DateTime ExpiresAt) IssueJwt(
        User user, IConfiguration configuration, HttpContext httpContext)
    {
        var key = configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("Jwt:Key is not configured.");
        var issuer = configuration["Jwt:Issuer"] ?? "archmind";
        var audience = configuration["Jwt:Audience"] ?? "archmind";
        var days = configuration.GetValue<int?>("Jwt:ExpirationDays") ?? 10;
        var expiresAt = DateTime.UtcNow.AddDays(days);

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        var creds = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var jwt = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt,
            signingCredentials: creds);

        var token = new JwtSecurityTokenHandler().WriteToken(jwt);

        httpContext.Response.Cookies.Append("archmind.sid", token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = httpContext.Request.IsHttps,
            Expires = expiresAt,
            Path = "/",
        });

        return (token, expiresAt);
    }

    private static bool IsValidEmail(string email)
    {
        try
        {
            return new EmailAddressAttribute().IsValid(email);
        }
        catch
        {
            return false;
        }
    }
}
