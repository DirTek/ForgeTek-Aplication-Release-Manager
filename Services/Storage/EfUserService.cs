using Microsoft.EntityFrameworkCore;
using ForgeTekApplicationReleaseManager.Data;
using ForgeTekApplicationReleaseManager.Models;
using ForgeTekApplicationReleaseManager.Services.Security;

namespace ForgeTekApplicationReleaseManager.Services.Storage;

/// <summary>EF Core-backed user store (shared across operators). Passwords are PBKDF2-hashed via
/// <see cref="PasswordHasher"/> — never stored in plaintext. Usernames match case-insensitively.</summary>
public sealed class EfUserService(IDbContextFactory<ForgeTekDbContext> factory) : IUserService
{
    private static string Key(string username) => username.Trim().ToLowerInvariant();

    public bool HasAnyUsers
    {
        get { using var db = factory.CreateDbContext(); return db.Users.AsNoTracking().Any(); }
    }

    public IReadOnlyList<AppUser> GetAll()
    {
        using var db = factory.CreateDbContext();
        return db.Users.AsNoTracking().Select(u => u.Payload).ToList()
            .Select(EfJson.Deserialize<AppUser>).ToList();
    }

    public AppUser? GetByName(string username)
    {
        using var db = factory.CreateDbContext();
        var row = db.Users.AsNoTracking().FirstOrDefault(u => u.UsernameKey == Key(username));
        return row is null ? null : EfJson.Deserialize<AppUser>(row.Payload);
    }

    public AppUser? Authenticate(string username, string password)
    {
        var user = GetByName(username);
        if (user is null) return null;
        return PasswordHasher.Verify(password, user.Salt, user.PasswordHash) ? user : null;
    }

    public AppUser Create(string username, string password, UserRole role)
    {
        username = username.Trim();
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username is required.");

        using var db = factory.CreateDbContext();
        if (db.Users.Any(u => u.UsernameKey == Key(username)))
            throw new InvalidOperationException($"A user named '{username}' already exists.");

        var (hash, salt) = PasswordHasher.Hash(password);
        var user = new AppUser { Username = username, PasswordHash = hash, Salt = salt, Role = role };
        db.Users.Add(new UserRow { UsernameKey = Key(username), Payload = EfJson.Serialize(user) });
        db.SaveChanges();
        return user;
    }

    public void SetRole(string username, UserRole role) => Mutate(username, u => u.Role = role);

    public void SetPassword(string username, string password) => Mutate(username, u =>
    {
        (u.PasswordHash, u.Salt) = PasswordHasher.Hash(password);
    });

    public void Delete(string username)
    {
        using var db = factory.CreateDbContext();
        var row = db.Users.FirstOrDefault(u => u.UsernameKey == Key(username));
        if (row is null) return;
        db.Users.Remove(row);
        db.SaveChanges();
    }

    private void Mutate(string username, Action<AppUser> change)
    {
        using var db = factory.CreateDbContext();
        var row = db.Users.FirstOrDefault(u => u.UsernameKey == Key(username));
        if (row is null) return;
        var user = EfJson.Deserialize<AppUser>(row.Payload);
        change(user);
        row.Payload = EfJson.Serialize(user);
        db.SaveChanges();
    }
}
