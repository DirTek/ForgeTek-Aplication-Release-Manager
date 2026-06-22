using ForgeTekApplicationReleaseManager.Models;

namespace ForgeTekApplicationReleaseManager.Services;

public interface IUserService
{
    /// <summary>True once at least one user exists — i.e. access protection is enabled.</summary>
    bool HasAnyUsers { get; }

    IReadOnlyList<AppUser> GetAll();
    AppUser? GetByName(string username);

    /// <summary>Returns the matching user when the password is correct, otherwise null.</summary>
    AppUser? Authenticate(string username, string password);

    /// <summary>Creates a user with a hashed password. Throws if the username already exists.</summary>
    AppUser Create(string username, string password, UserRole role);

    void SetRole(string username, UserRole role);
    void SetPassword(string username, string password);
    void Delete(string username);
}
