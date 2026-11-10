using System.ComponentModel.DataAnnotations;
using ConstructDpr.Core.Data;
using ConstructDpr.Shared.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ConstructDpr.Core;

/// <summary>
/// Sign-in for Windows apps that work straight on a shared SQL database (office SQL Server), without the web server.
/// Uses the same user table and password hashing as the server (ASP.NET Core Identity), so the same accounts work in
/// both ways and the database can later be put behind the server without changes.
/// </summary>
public sealed class DirectAccounts(Func<AppDbContext> dbFactory)
{
    private static readonly PasswordHasher<AppUser> Hasher = new();
    private const int MaxFailures = 8;

    public Caller Register(string email, string password, string displayName)
    {
        email = (email ?? "").Trim();
        if (!new EmailAddressAttribute().IsValid(email)) throw new WorkflowException("Enter a valid e-mail address.");
        if (string.IsNullOrWhiteSpace(displayName)) throw new WorkflowException("Enter your name.");
        if ((password ?? "").Length < 8) throw new WorkflowException("The password needs at least 8 characters.");
        using var db = dbFactory();
        var normal = email.ToUpperInvariant();
        if (db.Users.Any(u => u.NormalizedEmail == normal)) throw new WorkflowException($"{email} already has an account. Sign in instead.");
        var user = new AppUser
        {
            Id = Guid.NewGuid().ToString(), UserName = email, NormalizedUserName = normal, Email = email, NormalizedEmail = normal,
            DisplayName = displayName.Trim(), SecurityStamp = Guid.NewGuid().ToString("N").ToUpperInvariant(),
            ConcurrencyStamp = Guid.NewGuid().ToString(), LockoutEnabled = true,
        };
        user.PasswordHash = Hasher.HashPassword(user, password!);
        db.Users.Add(user);
        db.SaveChanges();
        return new Caller(user.Id, email, user.DisplayName);
    }

    public Caller SignIn(string email, string password)
    {
        using var db = dbFactory();
        var normal = (email ?? "").Trim().ToUpperInvariant();
        var user = db.Users.FirstOrDefault(u => u.NormalizedEmail == normal) ?? throw new UnauthorizedAccessException("Wrong e-mail or password.");
        if (user.LockoutEnd is { } until && until > DateTimeOffset.UtcNow)
            throw new UnauthorizedAccessException($"Too many wrong passwords. Try again after {until.ToLocalTime():HH:mm}.");
        var result = user.PasswordHash is null ? PasswordVerificationResult.Failed : Hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? "");
        if (result == PasswordVerificationResult.Failed)
        {
            user.AccessFailedCount++;
            if (user.AccessFailedCount >= MaxFailures) (user.LockoutEnd, user.AccessFailedCount) = (DateTimeOffset.UtcNow.AddMinutes(10), 0);
            db.SaveChanges();
            throw new UnauthorizedAccessException("Wrong e-mail or password.");
        }
        if (result == PasswordVerificationResult.SuccessRehashNeeded) user.PasswordHash = Hasher.HashPassword(user, password!);
        (user.AccessFailedCount, user.LockoutEnd) = (0, null);
        db.SaveChanges();
        return new Caller(user.Id, user.Email ?? email!, string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email ?? "" : user.DisplayName);
    }

    /// <summary>Re-reads a remembered account (the Windows app keeps the user id, never the password).</summary>
    public Caller? Find(string userId)
    {
        using var db = dbFactory();
        var u = db.Users.AsNoTracking().FirstOrDefault(x => x.Id == userId);
        return u is null || (u.LockoutEnd is { } until && until > DateTimeOffset.UtcNow) ? null
            : new Caller(u.Id, u.Email ?? "", string.IsNullOrWhiteSpace(u.DisplayName) ? u.Email ?? "" : u.DisplayName);
    }

    public void Rename(string userId, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) throw new WorkflowException("Enter your name.");
        using var db = dbFactory();
        var u = db.Users.First(x => x.Id == userId);
        u.DisplayName = displayName.Trim();
        foreach (var m in db.Members.Where(m => m.UserId == userId)) m.DisplayName = u.DisplayName;
        db.SaveChanges();
    }

    public void ChangePassword(string userId, string current, string next)
    {
        if ((next ?? "").Length < 8) throw new WorkflowException("The new password needs at least 8 characters.");
        using var db = dbFactory();
        var u = db.Users.First(x => x.Id == userId);
        if (u.PasswordHash is null || Hasher.VerifyHashedPassword(u, u.PasswordHash, current ?? "") == PasswordVerificationResult.Failed)
            throw new UnauthorizedAccessException("The current password is wrong.");
        u.PasswordHash = Hasher.HashPassword(u, next!);
        u.SecurityStamp = Guid.NewGuid().ToString("N").ToUpperInvariant();
        db.SaveChanges();
    }
}
