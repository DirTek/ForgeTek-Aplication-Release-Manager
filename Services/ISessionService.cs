using ForgeTekApplicationReleaseManager.Models;

namespace ForgeTekApplicationReleaseManager.Services;

public interface ISessionService
{
    AppUser? Current { get; }

    /// <summary>The signed-in app user's name, or the Windows user when no one is signed in.
    /// Used to attribute actions (publishes, setup generations) in activity feeds.</summary>
    string ActorName { get; }

    /// <summary>True when access protection is on AND a user is signed in.</summary>
    bool IsProtected { get; }

    void SignIn(AppUser user);
    void SignOut();

    // Role-derived permissions (all true when no users exist — the unprotected path).
    bool CanScan { get; }
    bool CanPublish { get; }
    bool CanManageSetups { get; }
    bool CanManageUsers { get; }

    /// <summary>May cast a binding approve/reject vote on a release (Admin or QA Tester).</summary>
    bool CanApprove { get; }

    /// <summary>May leave a non-binding review note on a release (any signed-in user).</summary>
    bool CanReviewNote { get; }
}
