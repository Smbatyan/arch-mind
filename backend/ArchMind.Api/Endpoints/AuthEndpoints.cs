using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using ArchMind.Core.Abstractions;
using ArchMind.Core.Entities;
using ArchMind.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace ArchMind.Api.Endpoints;

/// <summary>
/// Minimal API endpoints for custom email/password authentication.
/// All routes are mounted under /api/auth.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        // TODO: Restrict registration in production (invite-only or admin-created).
        group.MapPost("/register", RegisterAsync).AllowAnonymous();
        group.MapPost("/login", LoginAsync).AllowAnonymous();
        group.MapPost("/logout", (Delegate)LogoutAsync); // requires auth via fallback policy
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

        await SignInAsync(httpContext, user);

        return Results.Ok(new { user = new { id = user.Id, email = user.Email } });
    }

    private static async Task<IResult> LoginAsync(
        [FromBody] LoginRequest req,
        ArchMindDbContext db,
        IPasswordHasher hasher,
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

        await SignInAsync(httpContext, user);

        return Results.Ok(new { user = new { id = user.Id, email = user.Email } });
    }

    private static async Task<IResult> LogoutAsync(HttpContext httpContext)
    {
        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.NoContent();
    }

    private static IResult Me(HttpContext httpContext)
    {
        var idClaim = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var emailClaim = httpContext.User.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrEmpty(idClaim) || string.IsNullOrEmpty(emailClaim))
        {
            return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        return Results.Ok(new { user = new { id = idClaim, email = emailClaim } });
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------
    private static Task SignInAsync(HttpContext httpContext, User user)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        return httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties { IsPersistent = true });
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
