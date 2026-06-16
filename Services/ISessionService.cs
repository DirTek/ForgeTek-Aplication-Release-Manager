using ForgeTekUpdatePackager.Models;

namespace ForgeTekUpdatePackager.Services;

public interface ISessionService
{
    AppUser? Current { get; }

    /// <summary>True when access protection is on AND a user is signed in.</summary>
    bool IsProtected { get; }

    void SignIn(AppUser user);
    void SignOut();

    // Role-derived permissions (all true when no users exist — the unprotected path).
    bool CanScan { get; }
    bool CanPublish { get; }
    bool CanManageSetups { get; }
    bool CanManageUsers { get; }
}
