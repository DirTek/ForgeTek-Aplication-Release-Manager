namespace ForgeTekApplicationReleaseManager.Models;

/// <summary>Fixed roles that gate what a signed-in user may do.</summary>
public enum UserRole
{
    /// <summary>Full access, including managing users.</summary>
    Admin,

    /// <summary>Scan, package, and publish updates (no user management).</summary>
    Publisher,

    /// <summary>Scan only.</summary>
    Scanner,

    /// <summary>Create and generate setup bundles only.</summary>
    SetupBuilder,

    /// <summary>Reviews and approves (or rejects) releases before they can be published. Can scan to
    /// evaluate a build, but cannot publish on their own.</summary>
    QaTester,
}
